#Requires -Version 7.0
# FR-145 AC-189（system）。期待値は受入基準だけから決める。実際の文字起こしを CPU で実行するため非常に遅い（10 分の動画。1 Project あたり数分、2 Project）。
# 話し声のある 10 分の元動画（端末内の音声合成と ffmpeg で合成。外部送信・有料サービスなし）で、自動選択が有効な設定のとき:
#  (a) Project 作成から 60 秒（PARAM-013）以内に暫定選択が表示され、Project の作成・元動画の確認・Job の実行が待たされない。
#  (b) 最初の Job の文字起こしが終わり次第（待ちの上限＝600×PARAM-014＋PARAM-015＝660 秒）、話者数・話の速さを加えた再判定の結果が
#      次に作成する Job の初期選択と理由に反映され、実行済みの Job（最初の Job）は変わらない（出自「なし」・テンプレートなし）。
#  (c) 異常系: 手動でテンプレートを選んだ後は、再判定の待ちの上限を過ぎても更新されず（手動の選択のまま）、Job の出自は「手動」。
param([ValidateSet('AC-189')][string]$Ac = 'AC-189')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'StyleTemplate.common.ps1')
Set-StrictMode -Off

$script:LengthSec = 600
$script:ProvisionalLimitSec = 60                    # PARAM-013
$script:RejudgeLimitSec = $script:LengthSec * 1 + 60 # 元動画の長さ × PARAM-014 ＋ PARAM-015 = 660

function New-SpeechVideo10Min($sb, [string]$dir) {
    # 端末内の音声合成（System.Speech）で、2 話者（別の声）が交互に話す約 40 秒の WAV を作り、10 分に繰り返す。
    # 音声合成が使えないときは、話し声に近い振幅変調の音（代替）にする。
    # ffmpeg の H.264 エンコーダーは版により異なる（libx264 が無い LGPL 版がある）。使えるものを選ぶ。標準エラーはログに出す。
    $ff = Get-TestFfmpeg $sb
    $wav = Join-Path $dir 'speech.wav'
    $made = $false
    try {
        Add-Type -AssemblyName System.Speech
        $syn = New-Object System.Speech.Synthesis.SpeechSynthesizer
        try {
            $ja = @($syn.GetInstalledVoices() | Where-Object { $_.Enabled -and $_.VoiceInfo.Culture.Name -like 'ja*' })
            if ($ja.Count -gt 0) {
                $lines = @('今日は新しい動画編集の使い方について説明します。', 'まず元動画を取り込み、次に演出のテンプレートを選びます。', 'なるほど、そのあと字幕はどのように確認すればよいですか。', '字幕の画面で文字を確認して、必要なら直してください。書き出しの設定は最後に整えます。')
                $voices = @($ja | ForEach-Object { $_.VoiceInfo.Name })
                $female = $voices | Where-Object { $_ -match 'Haruka|Ayumi|Sayaka' } | Select-Object -First 1
                $male = $voices | Where-Object { $_ -match 'Ichiro' } | Select-Object -First 1
                $pair = @(@($female, $male) | Where-Object { $_ })
                if ($pair.Count -eq 0) { $pair = @($voices[0]) }
            } else {
                $lines = @('Today I will explain how to edit a video. First, import the source video.', 'Next, choose a style template. Then check the captions and prepare the export settings.')
                $pair = @($syn.GetInstalledVoices() | Where-Object { $_.Enabled } | ForEach-Object { $_.VoiceInfo.Name } | Select-Object -First 2)
            }
            $syn.SetOutputToWaveFile($wav)
            for ($i = 0; $i -lt $lines.Count; $i++) {
                if ($pair.Count -gt 0) { $syn.SelectVoice($pair[$i % $pair.Count]) }
                $syn.Speak($lines[$i])
            }
            $made = $true
        } finally { $syn.Dispose() }
    } catch { Write-Host "DIAG-SPEECH: System.Speech failed: $($_.Exception.Message)"; $made = $false }
    $video = Join-Path $dir 'source-10min.mp4'
    # 同じ内容の元動画の作り直し（約 3 分）を避けるため、作成済みのものを $env:TEMP に置いて再利用する（内容は固定。24 時間で作り直す）。
    $cache = Join-Path $env:TEMP 'fr145-source-10min-v1.mp4'
    if ((Test-Path -LiteralPath $cache) -and (Get-Item -LiteralPath $cache).Length -gt 1MB -and ((Get-Date) - (Get-Item -LiteralPath $cache).LastWriteTime).TotalHours -lt 24) {
        Copy-Item -LiteralPath $cache -Destination $video -Force; Write-Diag 'source video reused from cache'; return $video
    }
    $encoders = (& $ff -hide_banner -encoders 2>&1 | Out-String)
    $vc = 'mpeg4'
    foreach ($c in 'libx264', 'libopenh264', 'h264_mf') { if ($encoders -match "(?m)^\s*V\S*\s+$c\s") { $vc = $c; break } }
    $vopt = if ($vc -eq 'libx264') { @('-c:v', 'libx264', '-preset', 'ultrafast') } elseif ($vc -eq 'mpeg4') { @('-c:v', 'mpeg4', '-q:v', '5') } else { @('-c:v', $vc) }
    $v = @('-f', 'lavfi', '-i', "testsrc=size=1280x720:rate=15:duration=$($script:LengthSec)")
    if ($made -and (Test-Path -LiteralPath $wav) -and (Get-Item -LiteralPath $wav).Length -gt 1000) {
        $a = @('-stream_loop', '-1', '-i', $wav)
    } else {
        $a = @('-f', 'lavfi', '-i', "aevalsrc='0.4*sin(2*PI*140*t)*(0.6+0.4*sin(2*PI*4*t))+0.1*sin(2*PI*900*t)*sin(2*PI*2*t)':s=16000:d=$($script:LengthSec)")
    }
    $log = & $ff -y @v @a -t $script:LengthSec @vopt -pix_fmt yuv420p -c:a aac -ar 16000 -ac 1 -shortest $video 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $video)) {
        Write-Host "DIAG-FFMPEG: $ff (video encoder $vc) exit=$LASTEXITCODE`n$log"
        throw 'ffmpeg failed to create the 10-minute synthetic speech video'
    }
    try { Copy-Item -LiteralPath $video -Destination $cache -Force } catch {}
    $video
}
# 診断（時刻つき）。$env:TEMP に書く（コミットしない）。
$script:DiagFile = Join-Path $env:TEMP 'fr145-diag.log'
$script:T0 = [Diagnostics.Stopwatch]::StartNew()
function Write-Diag([string]$m) { $l = "[{0:HH:mm:ss} +{1,6:N0}s] {2}" -f (Get-Date), $script:T0.Elapsed.TotalSeconds, $m; Write-Host "DIAG: $l"; try { Add-Content -LiteralPath $script:DiagFile -Value $l -Encoding UTF8 } catch {} }
function Dump-VisibleNames($root, [string]$why) {
    try {
        $names = @($root.FindAll($script:Desc, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name } | Where-Object { $_ } | Select-Object -Unique -First 80)
        Write-Diag "$why visible: $($names -join ' | ')"
    } catch { Write-Diag "$why dump failed: $($_.Exception.Message)" }
}
function Get-SelectedTemplate($root) {
    foreach ($n in $script:TemplateNames) {
        $e = Find-AllByNameLike $root ("^" + [regex]::Escape($n) + "$") | Select-Object -First 1
        if ($e) {
            $sel = $null; try { $sel = $e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected } catch {}
            if ($sel) { return $n }
        }
    }
    return $null
}

function Wait-Selected($root, [int]$limitSec, [scriptblock]$Extra = { $true }) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    do {
        $t = Get-SelectedTemplate $root
        if ($t -and (& $Extra)) { return @{ Template = $t; Seconds = $sw.Elapsed.TotalSeconds } }
        Start-Sleep -Milliseconds 500
    } while ($sw.Elapsed.TotalSeconds -lt $limitSec)
    return $null
}

# (a) 用: 選択画面（「元動画」）が出ていないとき（「編集を開始」の後の自動遷移で押下が負けたとき）は「元動画」を押し直しながら待つ。
function Wait-SelectedEnsureView($root, [int]$limitSec) {
    $sw = [Diagnostics.Stopwatch]::StartNew(); $lastClick = -10; $clicks = 0
    do {
        $t = Get-SelectedTemplate $root
        if ($t) { return @{ Template = $t; Seconds = $sw.Elapsed.TotalSeconds; Clicks = $clicks } }
        $any = Find-AllByNameLike $root ('^(' + (($script:TemplateNames | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')$') | Select-Object -First 1
        if (-not $any -and ($sw.Elapsed.TotalSeconds - $lastClick) -ge 5) {
            $lastClick = $sw.Elapsed.TotalSeconds
            $b = Find-AllByNameLike $root '^元動画$' | Select-Object -First 1
            if ($b) { $clicks++; Write-Diag "(a) template list not visible at +$([int]$sw.Elapsed.TotalSeconds)s; re-pressing 元動画 (#$clicks)"; try { Invoke-Element $b } catch {} }
        }
        Start-Sleep -Milliseconds 500
    } while ($sw.Elapsed.TotalSeconds -lt $limitSec)
    return $null
}

function Get-LatestOrigin($sb, [string]$case) {
    $ws = Get-WorkspaceDir $sb
    $f = @(Get-ChildItem -LiteralPath $ws -Recurse -File -Filter 'template-origin.json' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime)
    if ($f.Count -eq 0) { throw "($case) no origin record found under workspace" }
    $text = Get-Content -LiteralPath $f[-1].FullName -Raw -Encoding UTF8
    $rec = $text | ConvertFrom-Json
    $val = if ($rec.Label) { [string]$rec.Label } else { [string]$rec.Origin }
    @{ Text = $text; Value = $val; RuleVersion = [string]$rec.RuleVersion }
}

# 「編集を開始」を押すと画面が処理中の画面へ自動で移る。テンプレート選択は「元動画」の画面にしか無いので、押して戻る（E2E-044 と同じ）。
function Start-FirstJob($root) { Invoke-Element (Find-ByName $root '編集を開始'); Invoke-Element (Find-ByName $root '元動画' 30) }

# hubradio.db の完了した Job の数（画面の「サムネイル: Job n、…完了」が出ない場合の代替判定）
function Get-CompletedJobCountFromDb {
    try {
        if (-not $script:AppSb) { return 0 }
        $db = Join-Path (Join-Path ([string]$script:AppSb.Work) 'workspace') 'hubradio.db'
        if (-not (Test-Path -LiteralPath $db)) { return 0 }
        $tmp = Join-Path ([IO.Path]::GetTempPath()) ('hr-done-' + [guid]::NewGuid().ToString('N') + '.db')
        Copy-SharedRead $db $tmp
        foreach ($x in "$db-wal", "$db-shm") { if (Test-Path -LiteralPath $x) { try { Copy-SharedRead $x ($tmp + $x.Substring($db.Length)) } catch {} } }
        $py = "import sqlite3,sys`ncon=sqlite3.connect(sys.argv[1])`nprint(con.execute(`"SELECT count(*) FROM jobs WHERE kind LIKE 'source-only.pipeline%' AND lower(state) IN ('completed','complete','succeeded','done')`").fetchone()[0])"
        $out = (& python -c $py $tmp 2>$null | Select-Object -First 1)
        Remove-Item "$tmp*" -Force -ErrorAction SilentlyContinue
        if ($out) { return [int]$out } else { return 0 }
    } catch { return 0 }
}
function Get-JobsSnapshot {
    try {
        $db = Join-Path (Join-Path ([string]$script:AppSb.Work) 'workspace') 'hubradio.db'
        if (-not (Test-Path -LiteralPath $db)) { return 'no-db' }
        $tmp = Join-Path ([IO.Path]::GetTempPath()) ('hr-snap-' + [guid]::NewGuid().ToString('N') + '.db')
        Copy-SharedRead $db $tmp
        foreach ($x in "$db-wal", "$db-shm") { if (Test-Path -LiteralPath $x) { try { Copy-SharedRead $x ($tmp + $x.Substring($db.Length)) } catch {} } }
        $py = "import sqlite3,sys`ncon=sqlite3.connect(sys.argv[1])`ncols=[r[1] for r in con.execute('pragma table_info(jobs)')]`nk=[c for c in cols if c.lower() in ('id','kind','state','stage','progress','updated_at','updatedat')]`nrows=con.execute('select '+','.join(k)+' from jobs').fetchall()`nprint(';'.join(str(r)[:120] for r in rows))"
        $out = (& python -c $py $tmp 2>&1 | Select-Object -First 1)
        Remove-Item "$tmp*" -Force -ErrorAction SilentlyContinue
        return "$out"
    } catch { return "snapshot failed: $($_.Exception.Message)" }
}
# Job の完了待ち: 画面の「サムネイル: Job n、…完了」か、DB の完了数が $baseline を超えたら完了（画面表示だけに依存しない）。失敗・アプリ終了・上限超過は例外。
function Wait-JobDone($root, [int]$jobNumber, [int]$baseline, [int]$timeoutSec = 1500) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    do {
        if ($script:AppProc -and $script:AppProc.HasExited) { throw "Job $jobNumber did not complete (app exited: $($script:AppProc.ExitCode))" }
        if (@(Find-AllByNameLike $root "^サムネイル: Job $jobNumber、.*完了$").Count -gt 0) { return }
        if ((Get-CompletedJobCountFromDb) -gt $baseline) { return }
        $dbFailed = Get-FailedJobFromDb
        if ($dbFailed) { throw "Job $jobNumber did not complete: db failed ($dbFailed)" }
        Start-Sleep -Seconds 2
    } while ($sw.Elapsed.TotalSeconds -lt $timeoutSec)
    throw "Job $jobNumber did not complete within $timeoutSec s"
}
# 次の Job を作成して完了まで待つ（New-TemplateJob と同じ手順。完了の待ちだけ Wait-JobDone）。
function New-TemplateJobDb($root, [int]$ordinal, [switch]$NoWait) {
    $baseline = Get-CompletedJobCountFromDb
    $originBefore = @(Get-ChildItem -LiteralPath (Get-WorkspaceDir $script:AppSb) -Recurse -File -Filter 'template-origin.json' -ErrorAction SilentlyContinue).Count
    Invoke-Element (Find-ByName $root '元動画' 30)
    Invoke-Element (Find-ByName $root '次に作成するJobの設定を開く' 60)
    Invoke-Element (Find-ByName $root '設定を閉じて戻る' 60)
    Start-Sleep -Seconds 2
    $sw = [Diagnostics.Stopwatch]::StartNew(); $nb = $null
    do {
        $nb = Find-AllByNameLike $root '^新しいJobを作成$' | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and $_.Current.IsEnabled } | Select-Object -First 1
        if (-not $nb) { Start-Sleep -Milliseconds 500 }
    } while (-not $nb -and $sw.Elapsed.TotalSeconds -lt 60)
    # Job の完了後、設定の確認が完了せず「新しいJobを作成」が無効のまま残ることがある（画面は「設定の確認を完了してください。」）。
    # 実測: 「設定を再読み込み」で有効になる。有効になるまで最大 900 秒・5 秒間隔で待ち、60 秒ごとに設定を開いて再読み込みして閉じる。
    if (-not $nb) {
        $sw2 = [Diagnostics.Stopwatch]::StartNew(); $lastRecover = -60
        do {
            $nb = Find-AllByNameLike $root '^新しいJobを作成$' | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and $_.Current.IsEnabled } | Select-Object -First 1
            if (-not $nb -and ($sw2.Elapsed.TotalSeconds - $lastRecover) -ge 60) {
                $lastRecover = $sw2.Elapsed.TotalSeconds
                try {
                    Invoke-Element (Find-ByName $root '次に作成するJobの設定を開く' 30)
                    $rl = Find-AllByNameLike $root '^設定を再読み込み$' | Where-Object { $_.Current.IsEnabled } | Select-Object -First 1
                    if ($rl) { Invoke-Element $rl; Start-Sleep -Seconds 3 }
                    Invoke-Element (Find-ByName $root '設定を閉じて戻る' 30)
                    Start-Sleep -Seconds 2
                } catch { Write-Host "retry-settings failed: $($_.Exception.Message)" }
            }
            if (-not $nb) { Start-Sleep -Seconds 5 }
        } while (-not $nb -and $sw2.Elapsed.TotalSeconds -lt 900)
    }
    if (-not $nb) { throw "button '新しいJobを作成' not found or disabled" }
    Invoke-Element $nb
    Start-Sleep -Seconds 1
    $ok = Find-AllByNameLike $root '^作成する$' | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button } | Select-Object -First 1
    if (-not $ok) { Start-Sleep -Seconds 3; $ok = Find-AllByNameLike $root '^作成する$' | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button } | Select-Object -First 1 }
    if (-not $ok) { throw "button '作成する' not found after pressing 新しいJobを作成" }
    Invoke-Element $ok
    if ($NoWait) {
        # AC-189 が要求するのは Job 作成までと、その Job の初期選択・出自。Job の完了は待たない（実行中に選択が変わらないことは呼び出し側で確かめる）。
        # Job の開始 = 作成した Job の起点の記録（template-origin.json）が現れること。
        $ws = Get-WorkspaceDir $script:AppSb
        $sw3 = [Diagnostics.Stopwatch]::StartNew()
        do {
            $n = @(Get-ChildItem -LiteralPath $ws -Recurse -File -Filter 'template-origin.json' -ErrorAction SilentlyContinue).Count
            if ($n -gt $originBefore) { Write-Diag "job $($ordinal + 1) started: origin record appeared after $([int]$sw3.Elapsed.TotalSeconds)s"; return }
            Start-Sleep -Milliseconds 500
        } while ($sw3.Elapsed.TotalSeconds -lt 90)
        throw "job $($ordinal + 1) was not started (no new origin record within 90 s)"
    }
    Wait-JobDone $root ($ordinal + 1) $baseline
}

$sb = New-AppSandbox
$p = $null; $p2 = $null; $sb2 = $null; Invoke-Test -Label "FR-145 $Ac" -Sandbox $sb -Body {
    try {
        $video = New-SpeechVideo10Min $sb $sb.Work
        $p = Start-App $sb; $root = Get-Root $p

        # ---- Project 1: 手動で触れない。暫定選択 → 最初の Job → 再判定 ----
        New-ProjectDraft $root $sb 'auto-10min' $video | Out-Null
        $created = [Diagnostics.Stopwatch]::StartNew()
        # (a) 元動画の確認ができる（待たされない）
        Invoke-Element (Find-ByName $root '元動画' 30)
        # (a) Job の実行が待たされない: 最初の Job をすぐ開始できる。
        #     テンプレート選択の画面は「編集を開始」の後にしか出ない（draft.isFinalized）ので、先に押す。
        Start-FirstJob $root
        $jobStart = [Diagnostics.Stopwatch]::StartNew()    # 起点＝Job の文字起こしの開始（Job 開始の直後）
        # (a) 暫定選択が Project 作成から 60 秒以内に表示される（残り時間で待つ。最低 5 秒は待つ）
        $remain = [Math]::Max(5, [int]($script:ProvisionalLimitSec - $created.Elapsed.TotalSeconds))
        $prov = Wait-SelectedEnsureView $root $remain
        if (-not $prov) { Dump-VisibleNames $root '(a) failure'; Write-Diag "(a) failure: created=+$([int]$created.Elapsed.TotalSeconds)s remain=$remain"; throw "(a) provisional selection was not displayed within $($script:ProvisionalLimitSec) s of project creation" }
        Write-Diag "(a) provisional '$($prov.Template)' at created+$([int]$created.Elapsed.TotalSeconds)s (clicks=$($prov.Clicks))"
        Write-Host "AC-189 (a): provisional '$($prov.Template)' after $([int]$created.Elapsed.TotalSeconds) s"
        # (b) 最初の Job の文字起こしが終わり次第、再判定の結果が初期選択と理由に反映される（待ちの上限 660 秒）
        $re = Wait-Selected $root $script:RejudgeLimitSec {
            [bool](Find-AllByNameLike $root '再判定' | Select-Object -First 1)
        }
        if (-not $re) { throw "(b) re-judgement (話者数・話の速さを加えた再判定) was not shown within $($script:RejudgeLimitSec) s of the transcription start" }
        $reason = (Find-AllByNameLike $root '再判定' | Select-Object -First 1).Current.Name
        if ($reason -notmatch '話者' -and $reason -notmatch '話の速さ|語数|文字数') { throw "(b) re-judgement reason lacks the speaker count / speech rate used: $reason" }
        # 理由の表示と初期選択は同時に更新されるとは限らない（UIA の反映の遅れ）。理由が示すテンプレートに選択が揃うのを短く待ち、揃わなければ失敗。
        $reasonTpl = $null
        foreach ($n in $script:TemplateNames) { if ($reason -match ('自動選択: ' + [regex]::Escape($n))) { $reasonTpl = $n } }
        if ($reasonTpl) {
            $sync = Wait-Selected $root 20 { (Get-SelectedTemplate $root) -eq $reasonTpl }
            if (-not $sync) { throw "(b) initial selection ('$(Get-SelectedTemplate $root)') does not match the re-judged template in the reason ('$reasonTpl'): $reason" }
            $re = $sync
        }
        Write-Diag "(b) re-judged '$($re.Template)' at job+$([int]$jobStart.Elapsed.TotalSeconds)s; newJobButtonEnabled=$([bool](Find-AllByNameLike $root '^新しいJobを作成$' | Where-Object { $_.Current.IsEnabled } | Select-Object -First 1))"
        Write-Host "AC-189 (b): re-judged '$($re.Template)' after $([int]$jobStart.Elapsed.TotalSeconds) s: $reason"
        # (b) 実行済みの Job（最初の Job）は変わらない: 出自「なし」・テンプレートなし。
        #     Job は全体で 1 つずつ順番に実行される（画面: 「実行中のJobが終わると、新しいJobを作成できます」）。次の Job の作成には最初の Job の完了が要る。
        #     （「新しいJobを作成」という名前の見出し・説明文も同名で有効に見えるため、再判定の時点で有効に見えるのはボタンではない。）
        Wait-JobDone $root 1 0
        Write-Diag 'job 1 done'
        Invoke-Element (Find-ByName $root '元動画' 30)   # 実行後は処理中の画面。選択画面へ戻る
        $first = Get-LatestOrigin $sb 'first'
        if ($first.Value -notmatch '^(なし|none)$') { throw "(b) executed first Job changed (origin must stay なし): $($first.Text)" }
        foreach ($n in $script:TemplateNames) { if ($first.Text -match [regex]::Escape($n)) { throw "(b) executed first Job got template '$n': $($first.Text)" } }
        # (b) 次に作成する Job の初期選択に反映: そのまま実行すると、再判定のテンプレート・出自「自動選択」・規則の版 2
        New-TemplateJobDb $root 1 -NoWait
        $second = Get-LatestOrigin $sb 'second'
        $firstAgain = @(Get-ChildItem -LiteralPath (Get-WorkspaceDir $sb) -Recurse -File -Filter 'template-origin.json' | Sort-Object LastWriteTime)[0]
        if ($firstAgain -and (Get-Content -LiteralPath $firstAgain.FullName -Raw -Encoding UTF8) -ne $first.Text) { throw "(b) executed first Job record changed after the next Job was created" }
        # 実行中（完了前）に選択が変わらない: 少し待っても Job 2 は完了せず、選択は再判定のテンプレートのまま
        Start-Sleep -Seconds 5
        if (@(Find-AllByNameLike $root '^サムネイル: Job 2、.*完了$').Count -gt 0) { Write-Diag 'job 2 already complete (not expected for 10 min video)' }
        Invoke-Element (Find-ByName $root '元動画' 30)
        $nowSel = Get-SelectedTemplate $root
        if ($nowSel -and $nowSel -ne $re.Template) { throw "(b) selection changed while the next Job was running: '$nowSel' (expected '$($re.Template)')" }
        if ($second.Value -notmatch '^(自動選択|auto)$') { throw "(b) next Job origin must be 自動選択: $($second.Text)" }
        if ($second.Text -notmatch [regex]::Escape($re.Template)) { throw "(b) next Job did not use the re-judged template '$($re.Template)': $($second.Text)" }
        if ($second.RuleVersion -notmatch '2') { throw "(b) rule version must be 2 (current): $($second.Text)" }

        # ---- Project 2（異常系 (c)）: 手動でテンプレートを選んだ後は更新されない ----
        # Job は全体で 1 つずつ順番に実行されるため、同じアプリのまま Project 2 を作ると、Project 1 の Job 2（約 10 分）の後ろで待たされ、
        # 文字起こしが始まらないまま再判定の待ちの上限が過ぎてしまう（(c) の検証にならない）。最初のアプリを閉じ（実行中の Job 2 は止まる）、
        # 別の Workspace の新しいアプリで Project 2 を作る。
        Stop-App $p
        $sb2 = New-AppSandbox
        $p2 = Start-App $sb2; $root = Get-Root $p2
        Write-Diag 'project 2: new app instance started'
        New-ProjectDraft $root $sb2 'manual-10min' $video | Out-Null
        Start-FirstJob $root    # 「編集を開始」の後に「元動画」へ戻って選択画面を出す
        $null = Wait-SelectedEnsureView $root $script:ProvisionalLimitSec
        $manual = '手作り感'
        Select-Template $root $manual    # 手動選択（「編集を開始」の後）
        $j2 = [Diagnostics.Stopwatch]::StartNew()
        # 再判定が更新するはずの時間（元動画の長さ×PARAM-014＋PARAM-015＝660 秒。文字起こしは実測 3〜4 分で終わる）が過ぎるまで、
        # 15 秒ごとに「手動の選択のまま・再判定の表示なし」を確かめる。最初の Job の完了は待たない。
        $next = 0
        while ($j2.Elapsed.TotalSeconds -lt $script:RejudgeLimitSec) {
            if ($j2.Elapsed.TotalSeconds -ge $next) { $next += 60; Write-Diag "(c) +$([int]$j2.Elapsed.TotalSeconds)s selected='$(Get-SelectedTemplate $root)'; db: $(Get-JobsSnapshot)" }
            $nowSel = Get-SelectedTemplate $root
            if ($nowSel -and $nowSel -ne $manual) { throw "(c) manual selection '$manual' was overwritten at +$([int]$j2.Elapsed.TotalSeconds) s (now '$nowSel')" }
            if (Find-AllByNameLike $root '再判定' | Select-Object -First 1) { throw "(c) re-judgement reason was displayed after a manual selection (+$([int]$j2.Elapsed.TotalSeconds) s)" }
            Start-Sleep -Seconds 10
        }
        Write-Diag "(c) window passed; db: $(Get-JobsSnapshot)"
        Invoke-Element (Find-ByName $root '元動画' 30)
        $now = Get-SelectedTemplate $root
        if ($now -ne $manual) { throw "(c) manual selection '$manual' was overwritten after the re-judgement window (now '$now')" }
        if (Find-AllByNameLike $root '再判定' | Select-Object -First 1) { throw '(c) re-judgement reason was displayed after a manual selection' }
        Wait-JobDone $root 1 0    # 次の Job の作成は、この Project の最初の Job の完了後にできる
        Write-Diag 'project 2 job 1 done'
        Invoke-Element (Find-ByName $root '元動画' 30)
        New-TemplateJobDb $root 1 -NoWait
        $m = Get-LatestOrigin $sb2 'manual'
        if ($m.Value -notmatch '^(手動|manual)$') { throw "(c) origin must be 手動: $($m.Text)" }
        if ($m.Text -notmatch [regex]::Escape($manual)) { throw "(c) applied template is not the manual selection '$manual': $($m.Text)" }
    } finally {
        Stop-App $p
        if ($p2) { Stop-App $p2 }
        if ($sb2) { Remove-AppSandbox $sb2 }   # 設定の戻しは新しい順（sb2 → sb。sb は Invoke-Test が戻す）
    }
}
