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
. (Join-Path $PSScriptRoot 'MediaTools.ps1')

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
    # アプリは常に %LOCALAPPDATA%\HubRadio.MovieCreator の workspace-location.json を読む（MainWindow.xaml.cs: applicationDataDirectory）。
    # WebView2Runtime の有無で場所を変えると、実利用者の Workspace を使ってしまう。
    $portable = $false
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
function Get-WorkspaceDir($sb) { Join-Path $sb.Work 'workspace' }   # New-AppSandbox が workspace-location.json で向ける場所
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
    # 配布形の起動は、同梱のモデルの確認などで 2 分近くかかることがある。画面（設定ボタン）が現れるまで待つ
    $root = Get-Root $p
    while (-not (Test-ByName $root '設定' 2) -and -not $p.HasExited -and $sw.Elapsed.TotalSeconds -lt 360) { }
    if ($p.HasExited) { throw "app exited: $($p.ExitCode)" }
    $script:AppProc = $p
    $script:AppSb = $sb
    $p
}
# 稼働中アプリの DB を読むためのコピー: FileShare.ReadWrite|Delete で開く（Copy-Item は書込み共有なしで開き、アプリの SQLite 書込みを
# 『attempt to write a readonly database』で失敗させるため使わない）。コピー元は読み取りのみ。
function Copy-SharedRead([string]$Path, [string]$Destination) {
    $in = $null; $out = $null
    try {
        $in = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        $out = [IO.File]::Open($Destination, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $in.CopyTo($out)
    } finally { if ($out) { $out.Dispose() }; if ($in) { $in.Dispose() } }
}
# 失敗時の保全: sandbox の workspace の job/ と hubradio.db（-wal/-shm）と logs を work/runs/<run-id>/e2e-failure-<case>/ にコピーする（後片付けで消えないように）
function Save-E2eFailureArtifacts([string]$case) {
    try {
        if (-not $script:AppSb) { return }
        $ws = Join-Path ([string]$script:AppSb.Work) 'workspace'
        if (-not (Test-Path -LiteralPath $ws)) { return }
        if (-not $case) { $case = $script:E2eCase }
        if (-not $case) { $case = 'unknown' }
        $case = $case -replace '[^\w\.-]', '_'
        $repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
        $rid = if ($env:RUN_ID) { $env:RUN_ID } elseif ($repo -match 'run-(\d+)') { $Matches[1] } else { '_diag' }
        $dest = Join-Path $repo "work\runs\$rid\e2e-failure-$case"
        $null = New-Item -ItemType Directory -Force -Path $dest
        foreach ($n in 'hubradio.db', 'hubradio.db-wal', 'hubradio.db-shm') {
            $f = Join-Path $ws $n
            if (Test-Path -LiteralPath $f) { try { Copy-SharedRead $f (Join-Path $dest $n) } catch {} }
        }
        foreach ($d in 'job', 'logs') {
            $s = Join-Path $ws $d
            if (Test-Path -LiteralPath $s) { Copy-Item -LiteralPath $s -Destination (Join-Path $dest $d) -Recurse -Force -ErrorAction SilentlyContinue }
        }
        $sl = Join-Path ([string]$script:AppSb.Work) 'logs'
        if (Test-Path -LiteralPath $sl) { Copy-Item -LiteralPath $sl -Destination (Join-Path $dest 'sandbox-logs') -Recurse -Force -ErrorAction SilentlyContinue }
        # 追加の保全: failure-diagnostics.json / result-receipt.json / ログ・診断・staging を相対パスを保ってコピー（大きい mp4/png/wav は一覧のみ）
        $work = [string]$script:AppSb.Work
        $big = '\.(mp4|png|wav|mov|mkv|jpg|jpeg)$'
        $listed = [System.Collections.Generic.List[string]]::new()
        $roots = @($work)
        $extra = @(Get-ChildItem -LiteralPath $work -Recurse -Force -File -ErrorAction SilentlyContinue)
        foreach ($f in $extra) {
            $rel = $f.FullName.Substring($work.Length).TrimStart('\', '/')
            $isDiag = $f.Name -like '*.failure-diagnostics.json' -or $f.Name -like '*.result-receipt.json'
            $isLogDir = $rel -match '(?i)(^|[\\/])(logs?|diagnostics?|diag|staging|artifact-staging|\.staging)[\\/]'
            $isLogFile = $f.Extension -in '.log', '.jsonl' -and $f.Length -lt 20MB
            if (-not ($isDiag -or $isLogDir -or $isLogFile)) { continue }
            $copied = 'listed-only'
            if ($f.Name -notmatch $big -and $f.Length -lt 50MB) {
                try {
                    $to = Join-Path (Join-Path $dest 'extra') $rel
                    $null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $to)
                    Copy-Item -LiteralPath $f.FullName -Destination $to -Force -ErrorAction Stop
                    $copied = 'copied'
                } catch { $copied = 'copy-failed' }
            }
            $listed.Add("$rel`t$($f.Length)`t$($f.LastWriteTime.ToString('o'))`t$copied")
            if ($f.Name -like '*.failure-diagnostics.json') {
                Write-Host "DIAG-FAILURE-JSON: ---- $rel ----"
                try { Get-Content -LiteralPath $f.FullName -Raw -ErrorAction Stop | Write-Host } catch { Write-Host "(read failed: $_)" }
            }
        }
        $listed.Insert(0, "path`tsize`tmtime`tstatus")
        Set-Content -LiteralPath (Join-Path $dest 'file-list.txt') -Value $listed -Encoding UTF8
        Write-Host "DIAG-JOBWAIT: saved failure artifacts to $dest"
    } catch { Write-Host "DIAG-JOBWAIT: save artifacts failed: $_" }
}
# workspace/hubradio.db に state=failed の Job があればその行（id|state）を返す（無ければ $null）
function Get-FailedJobFromDb {
    try {
        if (-not $script:AppSb) { return $null }
        $db = Join-Path (Join-Path ([string]$script:AppSb.Work) 'workspace') 'hubradio.db'
        if (-not (Test-Path -LiteralPath $db)) { return $null }
        $tmp = Join-Path ([IO.Path]::GetTempPath()) ('hr-fail-' + [guid]::NewGuid().ToString('N') + '.db')
        Copy-SharedRead $db $tmp
        foreach ($x in "$db-wal", "$db-shm") { if (Test-Path -LiteralPath $x) { try { Copy-SharedRead $x ($tmp + $x.Substring($db.Length)) } catch {} } }
        $py = "import sqlite3,sys`ncon=sqlite3.connect(sys.argv[1])`nfor r in con.execute(`"SELECT id, state FROM jobs WHERE lower(state)='failed' ORDER BY created_at LIMIT 1`"): print('|'.join(str(c) for c in r))"
        $out = (& python -c $py $tmp 2>$null | Select-Object -First 1)
        Remove-Item "$tmp*" -Force -ErrorAction SilentlyContinue
        if ($out) { return [string]$out } else { return $null }
    } catch { return $null }
}
# 診断: Job 完了待ちのタイムアウト／アプリ終了時の情報を work/runs/<run-id>/ のログ（と標準出力）に出す（失敗判定は変えない）
function Write-JobWaitDiag([string]$reason) {
    $lines = [System.Collections.Generic.List[string]]::new()
    try {
        $lines.Add("DIAG-JOBWAIT: reason=$reason at $((Get-Date).ToString('o'))")
        $p = $script:AppProc
        if ($p) {
            try { $p.Refresh() } catch {}
            if ($p.HasExited) { $lines.Add("DIAG-JOBWAIT: app pid=$($p.Id) exited exitCode=$($p.ExitCode) (0x$('{0:X}' -f [int]$p.ExitCode)) exitTime=$($p.ExitTime.ToString('o'))") }
            else { $lines.Add("DIAG-JOBWAIT: app pid=$($p.Id) still running") }
        } else { $lines.Add('DIAG-JOBWAIT: no app process recorded') }
        $work = if ($script:AppSb) { [string]$script:AppSb.Work } else { $null }
        if ($work -and (Test-Path -LiteralPath $work)) {
            $items = @(Get-ChildItem -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue)
            $lines.Add("DIAG-JOBWAIT: work=$work entries=$($items.Count)")
            $items | Where-Object { $_.PSIsContainer -and $_.FullName -match '(?i)job' } | Select-Object -First 50 | ForEach-Object { $lines.Add("DIAG-JOBWAIT: jobdir $($_.FullName)") }
            $logs = @($items | Where-Object { -not $_.PSIsContainer -and $_.Extension -in '.log', '.txt', '.jsonl' } | Sort-Object LastWriteTime -Descending | Select-Object -First 30)
            foreach ($f in $logs) { $lines.Add("DIAG-JOBWAIT: file $($f.FullName) size=$($f.Length) mtime=$($f.LastWriteTime.ToString('o'))") }
            foreach ($f in ($logs | Select-Object -First 5)) {
                $lines.Add("DIAG-JOBWAIT: ---- tail 80 of $($f.FullName) ----")
                try { Get-Content -LiteralPath $f.FullName -Tail 80 -ErrorAction Stop | ForEach-Object { $lines.Add($_) } } catch { $lines.Add("(read failed: $_)") }
            }
        } else { $lines.Add("DIAG-JOBWAIT: work dir not found ($work)") }
        # Job の状態: workspace/hubradio.db（jobs / job_attempts / job_progress）。sqlite3 があれば使い、無ければ python の sqlite3 で読む
        try {
            $ws = if ($work) { Join-Path $work 'workspace' } else { $null }
            $db = if ($ws) { Join-Path $ws 'hubradio.db' } else { $null }
            if ($db -and (Test-Path -LiteralPath $db)) {
                $q = "SELECT j.id, j.state, j.kind, j.current_attempt_number, j.created_at, j.updated_at, a.state, a.started_at, a.completed_at, a.error_code, (SELECT phase FROM job_progress p WHERE p.job_attempt_id = a.id ORDER BY p.sequence_number DESC LIMIT 1) AS phase FROM jobs j LEFT JOIN job_attempts a ON a.job_id = j.id AND a.sequence_number = j.current_attempt_number ORDER BY j.created_at;"
                $q2 = "SELECT p.recorded_at, p.job_id, p.sequence_number, p.phase, p.completed_units, p.total_units, p.message FROM job_progress p ORDER BY p.recorded_at DESC LIMIT 20;"
                $tmp = Join-Path ([IO.Path]::GetTempPath()) ('hr-diag-' + [guid]::NewGuid().ToString('N') + '.db')
                Copy-SharedRead $db $tmp
                foreach ($x in "$db-wal", "$db-shm") { if (Test-Path -LiteralPath $x) { try { Copy-SharedRead $x ($tmp + $x.Substring($db.Length)) } catch {} } }
                $sqlite = Get-Command sqlite3 -ErrorAction SilentlyContinue
                $lines.Add('DIAG-JOBWAIT: jobs (id|state|kind|attempt|created|updated|attemptState|started|completed|errorCode|phase)')
                if ($sqlite) {
                    & $sqlite.Source -readonly $tmp $q 2>&1 | ForEach-Object { $lines.Add("DIAG-JOBWAIT: job $_") }
                    $lines.Add('DIAG-JOBWAIT: recent 20 progress (recordedAt|jobId|seq|phase|done|total|message)')
                    & $sqlite.Source -readonly $tmp $q2 2>&1 | ForEach-Object { $lines.Add("DIAG-JOBWAIT: progress $_") }
                } else {
                    $py = "import sqlite3,sys`ncon=sqlite3.connect(sys.argv[1])`nfor q in sys.argv[2:]:`n    print('--')`n    [print('|'.join(str(c) for c in r)) for r in con.execute(q)]"
                    & python -c $py $tmp $q $q2 2>&1 | ForEach-Object { $lines.Add("DIAG-JOBWAIT: sql $_") }
                }
                Remove-Item "$tmp*" -Force -ErrorAction SilentlyContinue
            } else { $lines.Add("DIAG-JOBWAIT: hubradio.db not found ($db)") }
            # Job 履歴 JSON（JobHistoryLog: workspace/job/job-*.json）
            $hist = if ($ws) { @(Get-ChildItem -LiteralPath (Join-Path $ws 'job') -Filter 'job-*.json' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 20) } else { @() }
            foreach ($h in $hist) {
                # 生の JSON をそのまま出す（スキーマ前提にしない。exceptionType などの失敗情報が見える）
                try {
                    $raw = Get-Content -LiteralPath $h.FullName -Raw -ErrorAction Stop
                    if ($raw.Length -gt 4000) { $raw = $raw.Substring(0, 4000) + '...(truncated)' }
                    $lines.Add("DIAG-JOBWAIT: history $($h.Name) raw: $raw")
                }
                catch { $lines.Add("DIAG-JOBWAIT: history $($h.Name) (read failed: $_)") }
            }
        } catch { $lines.Add("DIAG-JOBWAIT: job state failed: $_") }
        # UIA 上の Job 行（サムネイル名に状態、工程名の表示）
        try {
            if ($p -and -not $p.HasExited) {
                $uroot = Get-Root $p
                $texts = @(Find-AllByNameLike $uroot '^サムネイル: Job |Job \d+|工程|処理中|待機|実行中|失敗|完了|取消' | ForEach-Object { [string]$_.Current.Name } | Select-Object -Unique -First 40)
                $lines.Add("DIAG-JOBWAIT: uia job texts count=$($texts.Count)")
                foreach ($t in $texts) { $lines.Add("DIAG-JOBWAIT: uia '$t'") }
            }
        } catch { $lines.Add("DIAG-JOBWAIT: uia texts failed: $_") }
        # アプリ配下の子プロセス（ffmpeg・python/Worker）
        try {
            if ($p) {
                $all = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue)
                $ids = [System.Collections.Generic.HashSet[int]]::new(); $null = $ids.Add([int]$p.Id)
                do { $n = $ids.Count; foreach ($c in $all) { if ($ids.Contains([int]$c.ParentProcessId)) { $null = $ids.Add([int]$c.ProcessId) } } } while ($ids.Count -ne $n)
                $kids = @($all | Where-Object { $ids.Contains([int]$_.ProcessId) -and $_.ProcessId -ne $p.Id })
                $lines.Add("DIAG-JOBWAIT: child processes count=$($kids.Count)")
                foreach ($k in $kids) {
                    $age = if ($k.CreationDate) { [int]((Get-Date) - $k.CreationDate).TotalSeconds } else { -1 }
                    $cl = [string]$k.CommandLine; if ($cl.Length -gt 200) { $cl = $cl.Substring(0, 200) }
                    $lines.Add("DIAG-JOBWAIT: child pid=$($k.ProcessId) name=$($k.Name) runSec=$age cmd=$cl")
                }
            }
        } catch { $lines.Add("DIAG-JOBWAIT: child processes failed: $_") }
        try {
            $since = (Get-Date).AddMinutes(-10)
            $ev = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = '.NET Runtime', 'Application Error', 'Windows Error Reporting'; StartTime = $since } -ErrorAction SilentlyContinue | Where-Object { $_.Message -match 'HubRadio' } | Select-Object -First 10)
            $lines.Add("DIAG-JOBWAIT: eventlog (HubRadio, last 10 min) count=$($ev.Count)")
            foreach ($e in $ev) { $lines.Add("DIAG-JOBWAIT: [$($e.TimeCreated.ToString('o'))] $($e.ProviderName) id=$($e.Id): $($e.Message)") }
        } catch { $lines.Add("DIAG-JOBWAIT: eventlog failed: $_") }
    } catch { $lines.Add("DIAG-JOBWAIT failed: $_") }
    $lines | ForEach-Object { Write-Host $_ }
    Save-E2eFailureArtifacts
    try {
        $repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
        $rid = if ($env:RUN_ID) { $env:RUN_ID } elseif ($repo -match 'run-(\d+)') { $Matches[1] } else { '_diag' }
        $dir = Join-Path $repo "work\runs\$rid"
        $null = New-Item -ItemType Directory -Force -Path $dir
        $lines | Add-Content -LiteralPath (Join-Path $dir 'e2e-jobwait-diag.log') -Encoding UTF8
    } catch { Write-Host "DIAG-JOBWAIT: log write failed: $_" }
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
function Get-OwnerWindowHandle($e) {
    $w = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $x = $e
    while ($x) {
        $h = $x.Current.NativeWindowHandle
        if ($h -ne 0 -and $x.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window) { return $h }
        $x = $w.GetParent($x)
    }
    if ($script:AppProc) { return $script:AppProc.MainWindowHandle }
    0
}
# 押す手段を順に試す: Invoke → SelectionItem.Select → ExpandCollapse.Expand → Toggle → 要素の中心をクリック → 最後の手段として SetFocus+Space
function Find-EditByName($root, [string]$name, [int]$timeoutSec = 20) {
    # 同じ名前のラベル（Text）と入力欄（Edit）があるため、入力欄だけを探す
    $sw = [Diagnostics.Stopwatch]::StartNew()
    do {
        $e = Find-AllByNameLike $root ('^' + [regex]::Escape($name) + '$') | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Edit } | Select-Object -First 1
        if ($e) { return $e }
        Start-Sleep -Milliseconds 300
    } while ($sw.Elapsed.TotalSeconds -lt $timeoutSec)
    throw "Edit not found: '$name'"
}
function Invoke-Element($e) {
    try { $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return } catch {}
    try { $e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); return } catch {}
    try { $e.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); return } catch {}
    try { $e.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); return } catch {}
    try {
        $rc = $e.Current.BoundingRectangle
        if ($rc.Width -gt 0 -and $rc.Height -gt 0) { Click-Element $e (Get-OwnerWindowHandle $e); return }
    } catch {}
    $e.SetFocus(); [System.Windows.Forms.SendKeys]::SendWait(' ')
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
        # 同一プロセスのデスクトップ直下の「開く」ウィンドウ（#32770 または名前が 開く/Open）も探す
        $pids = Get-AppPids $root.Process
        foreach ($t in $script:UIA::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $winCond)) {
            if ($pids.Contains([int]$t.Current.ProcessId) -and $t.Current.NativeWindowHandle -ne $main -and ($t.Current.ClassName -eq '#32770' -or $t.Current.Name -in '開く', 'Open', 'フォルダーの選択')) { return $t }
        }
        # 別プロセス（ブローカー）が持つ「開く」ウィンドウ: 名前が 開く/Open で、アプリ起動後に現れたもの
        foreach ($t in $script:UIA::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $winCond)) {
            if ($t.Current.Name -in '開く', 'Open') { return $t }
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

# 診断: 失敗時に見えていた UIA 要素（Button/Edit/ListItem）を列挙する
function Write-UiaDiag {
    try {
        if ($script:AppProc -and -not $script:AppProc.HasExited) {
            $r = Get-Root $script:AppProc
            $ct = [System.Windows.Automation.ControlType]
            $vis = @($r.FindAll($script:Desc, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.ControlType -in $ct::Button, $ct::Edit, $ct::ListItem } | ForEach-Object { "$($_.Current.ControlType.ProgrammaticName.Replace('ControlType.',''))|$($_.Current.Name)|en=$($_.Current.IsEnabled)|off=$($_.Current.IsOffscreen)" })
            Write-Host "DIAG-UIA: $($vis.Count) elements"; $vis | ForEach-Object { Write-Host "DIAG-UIA: $_" }
        } else { Write-Host 'DIAG-UIA: app not running' }
    } catch { Write-Host "DIAG-UIA failed: $_" }
}
function Invoke-Test([scriptblock]$Body, [string]$Label, $Sandbox) {
    $failed = $null
    $script:E2eCase = $Label
    try { & $Body } catch {
        $failed = $_
        Save-E2eFailureArtifacts $Label
        Write-UiaDiag    # アプリがまだ生きていれば（Body 内で Stop-App 済みでなければ）列挙する
    }
    finally { Remove-AppSandbox $Sandbox }
    if ($failed) { Write-Error "$Label failed: $failed"; exit 1 }
    Write-Host "$Label passed"; exit 0
}

# --- Job 成果物つき Project の作成（実際の操作で作る。合成した 2 秒の動画を元素材にする） ---
function New-SyntheticVideo($sb, [string]$dir) {
    $ff = Get-TestFfmpeg $sb
    $out = Join-Path $dir 'source.mp4'
    & $ff -y -f lavfi -i 'testsrc=size=1920x1080:rate=30:duration=3' -f lavfi -i 'sine=frequency=440:duration=3' -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest $out 2>$null
    if ($LASTEXITCODE -ne 0) {
        # libx264 の無い ffmpeg 向け（合成動画の作成だけの代替）
        & $ff -y -f lavfi -i 'testsrc=size=1920x1080:rate=30:duration=3' -f lavfi -i 'sine=frequency=440:duration=3' -c:v mpeg4 -pix_fmt yuv420p -c:a aac -shortest $out 2>$null
    }
    if ($LASTEXITCODE -ne 0) { throw 'ffmpeg failed to create synthetic video' }
    $out
}
function Get-TestFfmpeg($sb) {
    Resolve-TestMediaTool -Name ffmpeg -AppDir $sb.AppDir
}
# --- 合成画像（System.Drawing の PNG）。$Characters >= 2 なら、人物に見立てた色違いの楕円を横に並べた 1 枚画像にする ---
function New-SyntheticImage([string]$Path, [int]$Width = 640, [int]$Height = 640, [int]$Characters = 1, [string]$Color1 = '#3366CC', [string]$Color2 = '#CC9933') {
    Add-Type -AssemblyName System.Drawing
    $bmp = New-Object System.Drawing.Bitmap $Width, $Height
    try {
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        try {
            $c1 = [System.Drawing.ColorTranslator]::FromHtml($Color1); $c2 = [System.Drawing.ColorTranslator]::FromHtml($Color2)
            $rect = New-Object System.Drawing.Rectangle 0, 0, $Width, $Height
            $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $c1, $c2, 90.0
            $g.FillRectangle($br, $rect); $br.Dispose()
            $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $n = [Math]::Max(1, $Characters)
            $palette = @('#E74C3C', '#2ECC71', '#F1C40F', '#9B59B6', '#ECF0F1', '#1ABC9C')
            $slot = $Width / $n
            for ($i = 0; $i -lt $n; $i++) {
                $fc = [System.Drawing.ColorTranslator]::FromHtml($palette[$i % $palette.Count])
                $b = New-Object System.Drawing.SolidBrush $fc
                $cw = [int]($slot * 0.6); $cx = [int]($slot * $i + ($slot - $cw) / 2)
                $g.FillEllipse($b, $cx, [int]($Height * 0.12), $cw, [int]($Height * 0.76))   # 人物のシルエット相当
                $b.Dispose()
            }
        } finally { $g.Dispose() }
        $dir = Split-Path -Parent $Path
        if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $bmp.Dispose() }
    $Path
}
# --- 発話入りの合成動画（System.Speech の音声合成 → ffmpeg）。ASR が単語を認識できる明瞭な発話。利用可能な音声から英語を優先（無ければ既定の音声） ---
function New-SyntheticSpeechWav([string]$Path, [string]$Text = 'Hello and welcome to the radio show. Today we talk about music and movies. Thank you for listening.') {
    Add-Type -AssemblyName System.Speech
    $s = New-Object System.Speech.Synthesis.SpeechSynthesizer
    try {
        $en = $s.GetInstalledVoices() | Where-Object { $_.Enabled -and $_.VoiceInfo.Culture.Name -like 'en*' } | Select-Object -First 1
        if ($en) { $s.SelectVoice($en.VoiceInfo.Name) }
        $s.Rate = -1
        $fmt = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
        $s.SetOutputToWaveFile($Path, $fmt)
        $s.Speak($Text)
    } finally { $s.Dispose() }
    $Path
}
function New-SyntheticSpeechVideo($sb, [string]$dir, [string]$Text = 'Hello and welcome to the radio show. Today we talk about music and movies. Thank you for listening.') {
    $ff = Get-TestFfmpeg $sb
    $wav = New-SyntheticSpeechWav (Join-Path $dir 'speech.wav') $Text
    $out = Join-Path $dir 'source-speech.mp4'
    # 映像の長さを音声（WAV）の再生時間に合わせる。アプリの取り込み検証は、音声が映像より 250 ms を超えて短い素材を拒否するため
    $fs = [IO.File]::OpenRead($wav)
    try {
        $hdr = New-Object byte[] 44; [void]$fs.Read($hdr, 0, 44)
        $byteRate = [BitConverter]::ToInt32($hdr, 28)
        $dataLen = $fs.Length - 44
    } finally { $fs.Dispose() }
    if ($byteRate -le 0) { throw "invalid wav header: $wav" }
    $dur = [Math]::Round($dataLen / $byteRate, 3).ToString([Globalization.CultureInfo]::InvariantCulture)
    foreach ($vc in @('libx264', 'mpeg4')) {
        & $ff -y -f lavfi -i "testsrc=size=1920x1080:rate=30:duration=$dur" -i $wav -c:v $vc -pix_fmt yuv420p -c:a aac -ar 48000 -af apad -t $dur $out 2>$null
        if ($LASTEXITCODE -eq 0) {
            $a = Get-StreamDecodedSeconds $ff $out '0:a:0'
            $v = Get-StreamDecodedSeconds $ff $out '0:v:0'
            if ([Math]::Abs($a - $v) -gt 0.150) { throw ("synthetic speech video audio/video length mismatch: audio={0:N3}s video={1:N3}s" -f $a, $v) }
            $info = (& $ff -hide_banner -i $out 2>&1 | Out-String)
            if ($info -notmatch 'Audio:.*\b48000 Hz') { throw 'synthetic speech video audio is not 48000 Hz' }
            $vlog = (& $ff -v info -i $out -map 0:v:0 -f null - 2>&1 | Out-String)
            $fm = [regex]::Matches($vlog, 'frame=\s*(\d+)')
            if ($fm.Count -gt 0) {
                $frames = [int64]$fm[$fm.Count - 1].Groups[1].Value
                if (($frames * 48000) % 30 -ne 0) { throw "synthetic speech video frames x 48000/30 is not an integer: frames=$frames" }
            }
            return $out
        }
    }
    throw 'ffmpeg failed to create synthetic speech video'
}
# --- 長尺（20〜30 秒）の発話入り合成動画。短尺候補（FR-010 AC-002: 15〜60 秒）を作れるよう、文境界の明瞭な英語の文を重複なく並べる。自己検査: 長さ 20〜30 秒・1920x1080・48000 Hz ---
function New-SyntheticLongSpeechVideo($sb, [string]$dir) {
    $sentences = @(
        'Hello and welcome to the radio show.',
        'Today we talk about music and movies.',
        'First, let us talk about the music of the nineteen eighties.',
        'Many listeners love the bright sound of the synthesizer.',
        'Next, we turn to the movies that people watched last summer.',
        'The best film was a quiet story about a small town.',
        'Our guest explains how a good song can change a scene.',
        'She says that silence is as important as sound.',
        'Now we will answer a question from a listener.',
        'The question is about how to choose a movie for the weekend.',
        'Our advice is to start with a story that you already love.',
        'Thank you for listening, and see you next time.'
    )
    $ff = Get-TestFfmpeg $sb
    $text = $sentences -join ' '
    $out = $null
    for ($n = $sentences.Count; $n -ge 6; $n--) {
        $out = New-SyntheticSpeechVideo $sb $dir ($sentences[0..($n - 1)] -join ' ')
        $v = Get-StreamDecodedSeconds $ff $out '0:v:0'
        if ($v -ge 20 -and $v -le 30) { break }
        if ($v -lt 20) { break }   # 短すぎるなら下の検査で失敗させる
    }
    $info = (& $ff -hide_banner -i $out 2>&1 | Out-String)
    $dm = [regex]::Match($info, 'Duration: (\d+):(\d+):(\d+\.\d+)')
    if (-not $dm.Success) { throw 'cannot read duration of long synthetic speech video' }
    $len = [int]$dm.Groups[1].Value * 3600 + [int]$dm.Groups[2].Value * 60 + [double]::Parse($dm.Groups[3].Value, [Globalization.CultureInfo]::InvariantCulture)
    if ($len -lt 20 -or $len -gt 30) { throw ("long synthetic speech video length {0:N1}s is outside 20-30 s" -f $len) }
    if ($info -notmatch 'Video:.*\b1920x1080\b') { throw 'long synthetic speech video is not 1920x1080' }
    if ($info -notmatch 'Audio:.*\b48000 Hz') { throw 'long synthetic speech video audio is not 48000 Hz' }
    $out
}
function Get-StreamDecodedSeconds([string]$ff, [string]$Path, [string]$Map) {
    $log = (& $ff -v info -i $Path -map $Map -f null - 2>&1 | Out-String)
    $m = [regex]::Matches($log, 'time=(\d+):(\d+):(\d+\.\d+)')
    if ($m.Count -eq 0) { throw "cannot measure $Map of $Path" }
    $l = $m[$m.Count - 1]
    [int]$l.Groups[1].Value * 3600 + [int]$l.Groups[2].Value * 60 + [double]::Parse($l.Groups[3].Value, [Globalization.CultureInfo]::InvariantCulture)
}
function New-ProjectDraft($root, $sb, [string]$projectName, [string]$videoPath, [switch]$KeepInitialName) {
    # Project を作る手順の前半（動画を 1 本選ぶ → かんたん編集の画面 → Project 名）。「編集を開始」は押さない。初期値の名前を返す。
    # 「新しいProjectを作成」は OS の「開く」ダイアログで動画を 1 本選ぶ。選ぶとそのまま「かんたん編集」の画面になる。
    $create = Find-ByName $root '新しいProjectを作成'
    $sw0 = [Diagnostics.Stopwatch]::StartNew()
    while (-not $create.Current.IsEnabled -and $sw0.Elapsed.TotalSeconds -lt 60) { Start-Sleep -Milliseconds 500; $create = Find-ByName $root '新しいProjectを作成' }
    Invoke-Element $create
    $dlg = Find-Dialog $root 25
    if (-not $dlg) {
        # 押下が画面の準備前で無視された場合に備え、ボタンが有効なら 1 回だけ押し直す
        $create = Find-ByName $root '新しいProjectを作成'
        if ($create.Current.IsEnabled) { Invoke-Element $create }
        $dlg = Find-Dialog $root 40
    }
    if (-not $dlg) {
        $tops = (Get-AppTopLevels $root) | ForEach-Object { "[$($_.Current.ControlType.ProgrammaticName)|$($_.Current.ClassName)|$($_.Current.Name)|h=$($_.Current.NativeWindowHandle)]" }
        $wins = (Get-AppTopLevels $root) | ForEach-Object { $_.FindAll($script:Desc, [System.Windows.Automation.PropertyCondition]::new($script:UIA::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)) } | ForEach-Object { "[$($_.Current.ClassName)|$($_.Current.Name)|h=$($_.Current.NativeWindowHandle)]" }
        $dw = $script:UIA::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { "[$($_.Current.ClassName)|$($_.Current.Name)|pid=$($_.Current.ProcessId)]" }
        Write-Host "DIAG desktop: $($dw -join ' ') apppids=$((Get-AppPids $root.Process) -join ',')"
        Write-Host "DIAG tops: $($tops -join ' ') windows: $($wins -join ' ') main=$($root.Process.MainWindowHandle)"
        $seen = (Find-AllByNameLike $root '.') | ForEach-Object { $_.Current.Name } | Where-Object { $_ } | Select-Object -First 25
        throw "file dialog did not open after pressing 新しいProjectを作成 (enabled=$($create.Current.IsEnabled)). Visible: $($seen -join ' / ')"
    }
    Set-DialogForeground $dlg
    # ファイル名の欄へ ValuePattern で入力し「開く」を Invoke する。不可なら SendKeys に切り替える
    $done = $false
    try {
        # 「開く」ダイアログのファイル名欄（id 1148）は UIA では Pane。Win32 で ComboBox→Edit を辿り WM_SETTEXT で入力し、「開く」(id 1) を BM_CLICK する
        if (-not ('HubRadioSt.Dlg' -as [type])) {
            Add-Type -Namespace HubRadioSt -Name Dlg -MemberDefinition '[DllImport("user32.dll")] public static extern System.IntPtr GetDlgItem(System.IntPtr h, int id); [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern System.IntPtr FindWindowEx(System.IntPtr p, System.IntPtr a, string cls, string title); [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern System.IntPtr SendMessage(System.IntPtr h, int m, System.IntPtr w, string l); [DllImport("user32.dll")] public static extern System.IntPtr SendMessage(System.IntPtr h, int m, System.IntPtr w, System.IntPtr l);'
        }
        $h = [IntPtr]$dlg.Current.NativeWindowHandle
        $fnH = [HubRadioSt.Dlg]::GetDlgItem($h, 1148)
        $edit = [IntPtr]::Zero
        if ($fnH -ne [IntPtr]::Zero) {
            $cb = [HubRadioSt.Dlg]::FindWindowEx($fnH, [IntPtr]::Zero, 'ComboBox', $null)
            $edit = if ($cb -ne [IntPtr]::Zero) { [HubRadioSt.Dlg]::FindWindowEx($cb, [IntPtr]::Zero, 'Edit', $null) } else { [HubRadioSt.Dlg]::FindWindowEx($fnH, [IntPtr]::Zero, 'Edit', $null) }
        }
        $okH = [HubRadioSt.Dlg]::GetDlgItem($h, 1)
        if ($edit -ne [IntPtr]::Zero -and $okH -ne [IntPtr]::Zero) {
            $null = [HubRadioSt.Dlg]::SendMessage($edit, 0x0C, [IntPtr]::Zero, $videoPath)
            Start-Sleep -Milliseconds 400
            $null = [HubRadioSt.Dlg]::SendMessage($okH, 0xF5, [IntPtr]::Zero, [IntPtr]::Zero)
            $done = $true
        } else { Write-Host "DIAG: win32 edit not found (1148=$fnH edit=$edit ok=$okH)" }
        $idc = { param($id) [System.Windows.Automation.PropertyCondition]::new($script:UIA::AutomationIdProperty, $id) }
        $set = $false
        foreach ($fn in @(if ($done) { } else { $dlg.FindAll($script:Desc, (& $idc '1148')) })) {
            try { Set-Value $fn $videoPath; if ((Get-Value $fn) -eq $videoPath) { $set = $true; break } } catch {}
        }
        if ($set) {
            Start-Sleep -Milliseconds 400
            $ob = $dlg.FindFirst($script:Desc, (& $idc '1'))
            if ($ob) {
                try { $ob.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { Click-Element $ob $dlg.Current.NativeWindowHandle }
                $done = $true
            }
        } else { Write-Host 'DIAG: no 1148 element accepted ValuePattern' }
    } catch { Write-Host "DIAG value-input failed: $_" }
    if (-not $done) {
        Set-DialogForeground $dlg
        [System.Windows.Forms.SendKeys]::SendWait('%n')
        [System.Windows.Forms.SendKeys]::SendWait('^a')
        [System.Windows.Forms.SendKeys]::SendWait((ConvertTo-SendKeysText $videoPath))
        Start-Sleep -Milliseconds 400
        [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    }
    try { $nameBox = Find-EditByName $root 'Project名（この画面と一覧に表示される名前）' 120 }
    catch {
        $seen = (Find-AllByNameLike $root '.') | ForEach-Object { $_.Current.Name } | Where-Object { $_ } | Select-Object -First 25
        $od = Find-Dialog $root 2; if ($od) { Write-Host "DIAG dialog still open: [$($od.Current.ClassName)|$($od.Current.Name)]" }
        $tl = (Get-AppTopLevels $root) | ForEach-Object { "[$($_.Current.ClassName)|$($_.Current.Name)|enabled=$($_.Current.IsEnabled)]" }; Write-Host "DIAG tops: $($tl -join ' ')"
        throw "Project name field did not appear after choosing the video. Visible: $($seen -join ' / ')"
    }
    $initialName = [string](Get-Value $nameBox)
    if (-not $KeepInitialName) {
        Set-Value $nameBox $projectName
        Invoke-Element (Find-ByName $root 'Project名を保存')
    }
    $initialName
}
function Test-TabSelected($root, [string]$name) {
    # タブ（role=tab）の選択状態。同名の文章と区別するため TabItem だけを見る。
    $t = @(Find-AllByNameLike $root ('^' + [regex]::Escape($name) + '$') | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::TabItem })
    foreach ($e in $t) { try { if ($e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) { return $true } } catch {} }
    $false
}
function Answer-ConfirmDialog($root, [bool]$Accept, [int]$timeoutSec = 20) {
    # 削除の確認（ブラウザーの確認ダイアログ）。OK（確認）または キャンセル を押す。ボタンが見つからなければキーで答える。
    $re = if ($Accept) { '^(OK|確認|削除|削除する|はい)$' } else { '^(キャンセル|Cancel|いいえ)$' }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    do {
        $dlg = Find-Dialog $root 2
        $scope = if ($dlg) { $dlg } else { $root }
        $btn = Find-AllByNameLike $scope $re | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button } | Select-Object -First 1
        if ($btn) { Invoke-Element $btn; return }
        if ($dlg) { Set-DialogForeground $dlg; [System.Windows.Forms.SendKeys]::SendWait($(if ($Accept) { '{ENTER}' } else { '{ESC}' })); return }
    } while ($sw.Elapsed.TotalSeconds -lt $timeoutSec)
    throw 'confirmation dialog did not appear'
}
function New-ProjectWithCompletedJobs($root, $sb, [string]$projectName, [int]$jobCount, [string]$videoPath) {
    New-ProjectDraft $root $sb $projectName $videoPath | Out-Null
    # 最初の Job は「編集を開始」で作られる。完了は、その Job のサムネイルの名前（… 完了）で判定する
    Invoke-Element (Find-ByName $root '編集を開始')
    Wait-JobCompleted $root 1
    # 2 つ目以降は「新しいJobを作成」→ 確認の「作成する」
    try {
    for ($i = 2; $i -le $jobCount; $i++) {
        Invoke-Element (Find-ByName $root '元動画')    # 「新しいJobを作成」は「元動画」の画面にある
        # 設定を一度確認するまで「新しいJobを作成」は押せない（AC: 「次に作成するJobの設定を開く」→「設定を閉じて戻る」）
        Invoke-Element (Find-ByName $root '次に作成するJobの設定を開く' 60)
        Invoke-Element (Find-ByName $root '設定を閉じて戻る' 60)
        Start-Sleep -Seconds 2
        # 「新しいJobを作成」は見出し（Text）とボタンが同名。ボタンだけを押す（有効になるまで待つ）
        $sw2 = [Diagnostics.Stopwatch]::StartNew(); $nb = $null
        do {
            $nb = Find-AllByNameLike $root '^新しいJobを作成$' | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and $_.Current.IsEnabled } | Select-Object -First 1
            if (-not $nb) { Start-Sleep -Milliseconds 500 }
        } while (-not $nb -and $sw2.Elapsed.TotalSeconds -lt 60)
        if (-not $nb) { throw "button '新しいJobを作成' not found or disabled" }
        Invoke-Element $nb
        Start-Sleep -Seconds 1
        $cb2 = Find-AllByNameLike $root '^作成する$' | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button } | Select-Object -First 1
        if (-not $cb2) { Start-Sleep -Seconds 3; $cb2 = Find-AllByNameLike $root '^作成する$' | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button } | Select-Object -First 1 }
        if (-not $cb2) { throw "button '作成する' not found after pressing 新しいJobを作成" }
        Invoke-Element $cb2
        Wait-JobCompleted $root $i
    }
    } catch { Write-UiaDiag; throw }
}
function Wait-JobCompleted($root, [int]$jobNumber, [int]$timeoutSec = 900) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    do {
        if ($script:AppProc -and $script:AppProc.HasExited) {
            Write-JobWaitDiag "app exited while waiting for Job $jobNumber (after $([int]$sw.Elapsed.TotalSeconds) s)"
            throw "Job $jobNumber did not complete within $timeoutSec s (app exited: $($script:AppProc.ExitCode))"
        }
        if (@(Find-AllByNameLike $root "^サムネイル: Job $jobNumber、.*完了$").Count -gt 0) { return }
                # 失敗の検知: UIA の行（「状態: 失敗」の形でも、「失敗」だけの行でも）か、hubradio.db の jobs.state=failed
                $failed = @(Find-AllByNameLike $root '操作を完了できませんでした|^サムネイル: Job \d+、.*(失敗|取消)$|^Job \d+.*失敗|状態: ?失敗|^失敗$')
                $dbFailed = Get-FailedJobFromDb
                if ($failed.Count -gt 0 -or $dbFailed) {
                    Write-JobWaitDiag "Job $jobNumber failed (uia=$($failed.Count) db=$dbFailed) after $([int]$sw.Elapsed.TotalSeconds) s"
                    $msg = (Find-AllByNameLike $root '編集を開始できません|失敗') | Select-Object -First 1
                    $why = if ($msg) { $msg.Current.Name } else { "状態: 失敗 (db: $dbFailed)" }
                    throw "Job $jobNumber did not complete: $why"
                }
                Start-Sleep -Seconds 2
    } while ($sw.Elapsed.TotalSeconds -lt $timeoutSec)
    Write-JobWaitDiag "timeout waiting for Job $jobNumber"
    throw "Job $jobNumber did not complete within $timeoutSec s"
}


