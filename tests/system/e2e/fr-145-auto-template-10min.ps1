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
    # 端末内の音声合成（System.Speech）で話し声の WAV を作り、10 分に繰り返す。音声合成が使えないときは、話し声に近い振幅変調の音（代替）にする。
    $ff = Get-TestFfmpeg $sb
    $wav = Join-Path $dir 'speech.wav'
    $made = $false
    try {
        Add-Type -AssemblyName System.Speech
        $syn = New-Object System.Speech.Synthesis.SpeechSynthesizer
        try {
            $ja = @($syn.GetInstalledVoices() | Where-Object { $_.Enabled -and $_.VoiceInfo.Culture.Name -like 'ja*' }) | Select-Object -First 1
            if ($ja) { $syn.SelectVoice($ja.VoiceInfo.Name); $text = '今日は新しい動画編集の使い方について説明します。まず元動画を取り込み、次に演出のテンプレートを選びます。そのあと字幕を確認して、書き出しの設定を整えます。' }
            else { $text = 'Today I will explain how to edit a video. First, import the source video. Next, choose a style template. Then check the captions and prepare the export settings.' }
            $syn.SetOutputToWaveFile($wav); $syn.Speak($text); $made = $true
        } finally { $syn.Dispose() }
    } catch { $made = $false }
    $video = Join-Path $dir 'source-10min.mp4'
    $v = @('-f', 'lavfi', '-i', "testsrc=size=1280x720:rate=15:duration=$($script:LengthSec)")
    if ($made -and (Test-Path -LiteralPath $wav)) {
        $a = @('-stream_loop', '-1', '-i', $wav)
    } else {
        $a = @('-f', 'lavfi', '-i', "aevalsrc='0.4*sin(2*PI*140*t)*(0.6+0.4*sin(2*PI*4*t))+0.1*sin(2*PI*900*t)*sin(2*PI*2*t)':s=16000:d=$($script:LengthSec)")
    }
    & $ff -y @v @a -t $script:LengthSec -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac -shortest $video 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'ffmpeg failed to create the 10-minute synthetic speech video' }
    $video
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

function Get-LatestOrigin($sb, [string]$case) {
    $ws = Get-WorkspaceDir $sb
    $f = @(Get-ChildItem -LiteralPath $ws -Recurse -File -Filter 'template-origin.json' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime)
    if ($f.Count -eq 0) { throw "($case) no origin record found under workspace" }
    $text = Get-Content -LiteralPath $f[-1].FullName -Raw -Encoding UTF8
    $rec = $text | ConvertFrom-Json
    $val = if ($rec.Label) { [string]$rec.Label } else { [string]$rec.Origin }
    @{ Text = $text; Value = $val; RuleVersion = [string]$rec.RuleVersion }
}

function Start-FirstJob($root) { Invoke-Element (Find-ByName $root '編集を開始') }

$sb = New-AppSandbox
$p = $null
Invoke-Test -Label "FR-145 $Ac" -Sandbox $sb -Body {
    try {
        $video = New-SpeechVideo10Min $sb $sb.Work
        $p = Start-App $sb; $root = Get-Root $p

        # ---- Project 1: 手動で触れない。暫定選択 → 最初の Job → 再判定 ----
        New-ProjectDraft $root $sb 'auto-10min' $video | Out-Null
        $created = [Diagnostics.Stopwatch]::StartNew()
        # (a) 元動画の確認ができる（待たされない）
        Invoke-Element (Find-ByName $root '元動画' 30)
        # (a) 暫定選択が 60 秒以内に表示される
        $prov = Wait-Selected $root $script:ProvisionalLimitSec
        if (-not $prov) { throw "(a) provisional selection was not displayed within $($script:ProvisionalLimitSec) s of project creation" }
        Write-Host "AC-189 (a): provisional '$($prov.Template)' after $([int]$created.Elapsed.TotalSeconds) s"
        # (a) Job の実行が待たされない: 最初の Job をすぐ開始できる
        Start-FirstJob $root
        $jobStart = [Diagnostics.Stopwatch]::StartNew()    # 起点＝Job の文字起こしの開始（Job 開始の直後）
        # (b) 最初の Job の文字起こしが終わり次第、再判定の結果が初期選択と理由に反映される（待ちの上限 660 秒）
        $re = Wait-Selected $root $script:RejudgeLimitSec {
            [bool](Find-AllByNameLike $root '再判定' | Select-Object -First 1)
        }
        if (-not $re) { throw "(b) re-judgement (話者数・話の速さを加えた再判定) was not shown within $($script:RejudgeLimitSec) s of the transcription start" }
        $reason = (Find-AllByNameLike $root '再判定' | Select-Object -First 1).Current.Name
        if ($reason -notmatch '話者' -and $reason -notmatch '話の速さ|語数|文字数') { throw "(b) re-judgement reason lacks the speaker count / speech rate used: $reason" }
        Write-Host "AC-189 (b): re-judged '$($re.Template)' after $([int]$jobStart.Elapsed.TotalSeconds) s: $reason"
        # (b) 実行済みの Job（最初の Job）は変わらない: 出自「なし」・テンプレートなし
        Wait-JobCompleted $root 1
        $first = Get-LatestOrigin $sb 'first'
        if ($first.Value -notmatch '^(なし|none)$') { throw "(b) executed first Job changed (origin must stay なし): $($first.Text)" }
        foreach ($n in $script:TemplateNames) { if ($first.Text -match [regex]::Escape($n)) { throw "(b) executed first Job got template '$n': $($first.Text)" } }
        # (b) 次に作成する Job の初期選択に反映: そのまま実行すると、再判定のテンプレート・出自「自動選択」・規則の版 2
        New-TemplateJob $root 1
        $second = Get-LatestOrigin $sb 'second'
        if ($second.Value -notmatch '^(自動選択|auto)$') { throw "(b) next Job origin must be 自動選択: $($second.Text)" }
        if ($second.Text -notmatch [regex]::Escape($re.Template)) { throw "(b) next Job did not use the re-judged template '$($re.Template)': $($second.Text)" }
        if ($second.RuleVersion -notmatch '2') { throw "(b) rule version must be 2 (current): $($second.Text)" }

        # ---- Project 2（異常系 (c)）: 手動でテンプレートを選んだ後は更新されない ----
        Invoke-Element (Find-ByName $root 'Project一覧へ戻る' 30)
        New-ProjectDraft $root $sb 'manual-10min' $video | Out-Null
        $null = Wait-Selected $root $script:ProvisionalLimitSec
        $manual = '手作り感'
        Select-Template $root $manual
        Start-FirstJob $root
        $j2 = [Diagnostics.Stopwatch]::StartNew()
        Wait-JobCompleted $root 1
        # 文字起こしが終わった後、再判定が更新するはずの時間（上限の残り 30 秒まで）を待っても、手動の選択のまま・再判定の表示なし
        $rest = [Math]::Max(0, [Math]::Min($script:RejudgeLimitSec - [int]$j2.Elapsed.TotalSeconds, 90))
        Start-Sleep -Seconds $rest
        $now = Get-SelectedTemplate $root
        if ($now -ne $manual) { throw "(c) manual selection '$manual' was overwritten after the re-judgement window (now '$now')" }
        if (Find-AllByNameLike $root '再判定' | Select-Object -First 1) { throw '(c) re-judgement reason was displayed after a manual selection' }
        New-TemplateJob $root 1
        $m = Get-LatestOrigin $sb 'manual'
        if ($m.Value -notmatch '^(手動|manual)$') { throw "(c) origin must be 手動: $($m.Text)" }
        if ($m.Text -notmatch [regex]::Escape($manual)) { throw "(c) applied template is not the manual selection '$manual': $($m.Text)" }
    } finally { Stop-App $p }
}
