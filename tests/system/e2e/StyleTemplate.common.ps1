#Requires -Version 7.0
# FR-136〜FR-139 の System Test 共通部品。期待値は受入基準だけから決める。
# UI 名は受入基準の語（演出テンプレート）を使う。Job の保存内容は Workspace 配下の JSON を文字列として調べる。
. (Join-Path $PSScriptRoot 'HubRadioUia.ps1')

$script:TemplateNames = @('解説', 'ショート飛び出し', 'バラエティ', '対談番組', '縦型3段', 'キャラクター対話', '手作り感')

function Open-TemplateList($root) {
    # 新規 Job の作成画面で、演出テンプレートの一覧を開く。
    $e = Find-ByName $root '演出テンプレート'
    Invoke-Element $e
    return $e
}

function Select-Template($root, [string]$name) {
    Invoke-Element (Find-ByName $root '演出テンプレート')
    Invoke-Element (Find-ByName $root $name)
}

function Get-WorkspaceText($sb, [string]$filter = '*.json') {
    $ws = Join-Path $sb.AppDir 'workspace'
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
    Invoke-Element (Find-ByName $root '実行')
    Wait-JobCompleted $root $jobNumber | Out-Null
}

function Get-DeliverableCaptions($sb) {
    # 成果物の字幕（SRT/VTT/編集計画 JSON）を集める。
    $ws = Join-Path $sb.AppDir 'workspace'
    @(Get-ChildItem -LiteralPath $ws -Recurse -File -Include *.srt, *.vtt, *.json -ErrorAction SilentlyContinue)
}
