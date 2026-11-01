#Requires -Version 7.0
# FR-136〜FR-139 の System Test 共通部品。期待値は受入基準だけから決める。
# UI 名は受入基準の語（演出テンプレート）を使う。Job の保存内容は Workspace 配下の JSON を文字列として調べる。
$ErrorActionPreference = 'Stop'  # dot-source の ParserError/未定義関数を終了コード 0 で終わらせない
. (Join-Path $PSScriptRoot 'HubRadioUia.ps1')

$script:TemplateNames = @('解説', 'ショート飛び出し', 'バラエティ', '対談番組', '縦型3段', 'キャラクター対話', '手作り感')

function Open-TemplateList($root) {
    # 新規 Job の作成画面で、演出テンプレートの一覧を開く。
    $e = Find-ByName $root '演出テンプレート'
    Invoke-Element $e
    return $e
}

function Select-Template($root, [string]$name) {
    # 演出テンプレートは「元動画」の画面の「新しいJobを作成」の節にある。ボタン「演出テンプレート」（一覧の先頭へ移すだけ）と、各テンプレート名のラジオ。
    Invoke-Element (Find-ByName $root '元動画')
    Invoke-Element (Find-ByName $root '演出テンプレート')
    Invoke-Element (Find-ByName $root $name)
}

function New-TemplateJob($root, [int]$ordinal) {
    # 「実行」ボタンは無い。最初の Job は「編集を開始」で作られるので、テンプレートを付けた Job は Job 2 以降になる（ordinal は 1 始まり → Job ordinal+1）。
    # 既存の Job 作成手順: 次に作成するJobの設定を開く → 設定を閉じて戻る → 新しいJobを作成 → 作成する。
    Invoke-Element (Find-ByName $root '元動画')
    Invoke-Element (Find-ByName $root '次に作成するJobの設定を開く' 60)
    Invoke-Element (Find-ByName $root '設定を閉じて戻る' 60)
    Start-Sleep -Seconds 2
    $sw = [Diagnostics.Stopwatch]::StartNew(); $nb = $null
    do {
        $nb = Find-AllByNameLike $root '^新しいJobを作成$' | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and $_.Current.IsEnabled } | Select-Object -First 1
        if (-not $nb) { Start-Sleep -Milliseconds 500 }
    } while (-not $nb -and $sw.Elapsed.TotalSeconds -lt 60)
    if (-not $nb) { throw "button '新しいJobを作成' not found or disabled" }
    Invoke-Element $nb
    Start-Sleep -Seconds 1
    $ok = Find-AllByNameLike $root '^作成する$' | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button } | Select-Object -First 1
    if (-not $ok) { Start-Sleep -Seconds 3; $ok = Find-AllByNameLike $root '^作成する$' | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button } | Select-Object -First 1 }
    if (-not $ok) { throw "button '作成する' not found after pressing 新しいJobを作成" }
    Invoke-Element $ok
    Wait-JobCompleted $root ($ordinal + 1) | Out-Null
}

function Set-OpenDialogFile($root, [string]$path) {
    # OS の「開く」ダイアログ（<input type=file> が開く）: ファイル名欄へ WM_SETTEXT で入力し「開く」(id 1) を押す。不可なら SendKeys。
    $dlg = Find-Dialog $root 25
    if (-not $dlg) { throw 'file dialog did not open' }
    Set-DialogForeground $dlg
    if (-not ('HubRadioSt.Dlg' -as [type])) {
        Add-Type -Namespace HubRadioSt -Name Dlg -MemberDefinition '[DllImport("user32.dll")] public static extern System.IntPtr GetDlgItem(System.IntPtr h, int id); [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern System.IntPtr FindWindowEx(System.IntPtr p, System.IntPtr a, string cls, string title); [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern System.IntPtr SendMessage(System.IntPtr h, int m, System.IntPtr w, string l); [DllImport("user32.dll")] public static extern System.IntPtr SendMessage(System.IntPtr h, int m, System.IntPtr w, System.IntPtr l);'
    }
    $h = [IntPtr]$dlg.Current.NativeWindowHandle
    $fnH = [HubRadioSt.Dlg]::GetDlgItem($h, 1148); $edit = [IntPtr]::Zero
    if ($fnH -ne [IntPtr]::Zero) {
        $cb = [HubRadioSt.Dlg]::FindWindowEx($fnH, [IntPtr]::Zero, 'ComboBox', $null)
        $edit = if ($cb -ne [IntPtr]::Zero) { [HubRadioSt.Dlg]::FindWindowEx($cb, [IntPtr]::Zero, 'Edit', $null) } else { [HubRadioSt.Dlg]::FindWindowEx($fnH, [IntPtr]::Zero, 'Edit', $null) }
    }
    $okH = [HubRadioSt.Dlg]::GetDlgItem($h, 1)
    if ($edit -ne [IntPtr]::Zero -and $okH -ne [IntPtr]::Zero) {
        $null = [HubRadioSt.Dlg]::SendMessage($edit, 0x0C, [IntPtr]::Zero, $path)
        Start-Sleep -Milliseconds 400
        $null = [HubRadioSt.Dlg]::SendMessage($okH, 0xF5, [IntPtr]::Zero, [IntPtr]::Zero)
    } else {
        [System.Windows.Forms.SendKeys]::SendWait('%n'); [System.Windows.Forms.SendKeys]::SendWait('^a')
        [System.Windows.Forms.SendKeys]::SendWait((ConvertTo-SendKeysText $path)); Start-Sleep -Milliseconds 400
        [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    }
    Start-Sleep -Seconds 1
}

function Register-Portrait($root, [string]$characterName, [string]$imagePath) {
    # 「立ち絵の登録」: キャラクター名 → 画像ファイル（ファイル選択ダイアログ）→ 使用権を確認済み → 登録する。
    Invoke-Element (Find-ByName $root '元動画')
    Set-Value (Find-EditByName $root 'キャラクター名') $characterName
    $pick = $null; $sw = [Diagnostics.Stopwatch]::StartNew()
    do {
        $pick = Find-AllByNameLike $root '^画像ファイル' | Where-Object { $_.Current.ControlType -ne [System.Windows.Automation.ControlType]::Text } | Select-Object -First 1
        if (-not $pick) { Start-Sleep -Milliseconds 300 }
    } while (-not $pick -and $sw.Elapsed.TotalSeconds -lt 20)
    if (-not $pick) { throw 'file input 画像ファイル not found' }
    Invoke-Element $pick
    Set-OpenDialogFile $root $imagePath
    Invoke-Element (Find-ByName $root '使用権を確認済み')
    Invoke-Element (Find-ByName $root '登録する')
    $null = Find-ByName $root '立ち絵を登録しました。使用権の確認状態も保存しました。' 30
}

function Get-WorkspaceDir($sb) {
    # 通常ビルドの Workspace は New-AppSandbox が設定で向けた $sb.Work\workspace。配布形（portable）はアプリのフォルダー内。
    if ($sb.Portable) { Join-Path $sb.AppDir 'workspace' } else { Join-Path $sb.Work 'workspace' }
}

function Get-RenderRecord($sb, [int]$ordinal = 0) {
    # Job のフォルダーの style-template-render.json（話者・色・単語・立ち絵）。ordinal は 1 始まりで古い順、0 は最新。
    $ws = Get-WorkspaceDir $sb
    $f = @(Get-ChildItem -LiteralPath $ws -Recurse -File -Filter 'style-template-render.json' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime)
    if ($f.Count -eq 0) { throw 'style-template-render.json not found under workspace' }
    $pick = if ($ordinal -le 0) { $f[-1] } else { if ($ordinal -gt $f.Count) { throw "render record #$ordinal not found" }; $f[$ordinal - 1] }
    Get-Content -LiteralPath $pick.FullName -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 32
}

function Get-WorkspaceText($sb, [string]$filter = '*.json') {
    $ws = Get-WorkspaceDir $sb
    @(Get-ChildItem -LiteralPath $ws -Recurse -File -Filter $filter -ErrorAction SilentlyContinue |
        ForEach-Object { [pscustomobject]@{ Path = $_.FullName; Text = (Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8) } })
}

function Get-TemplateJobRecord($sb) {
    # 演出テンプレートの ID・版を持つ Job の記録を返す。
    $r = Get-WorkspaceText $sb | Where-Object { $_.Text -match '(?i)(templateId|template_id|演出テンプレート)' }
    if (-not $r) { throw 'no job record with template id found under workspace' }
    return $r
}

function New-JobWithTemplate($root, $sb, [string]$template, [string]$video, [int]$jobNumber = 1) {
    $null = New-ProjectWithCompletedJobs $root $sb ("tpl-" + [guid]::NewGuid().ToString('N').Substring(0, 6)) 0 $video
    if ($template) { Select-Template $root $template }
    New-TemplateJob $root $jobNumber
}

function Get-DeliverableCaptions($sb) {
    # 成果物の字幕（SRT/VTT/編集計画 JSON）を集める。
    $ws = Get-WorkspaceDir $sb
    @(Get-ChildItem -LiteralPath $ws -Recurse -File -Include *.srt, *.vtt, *.json -ErrorAction SilentlyContinue)
}
