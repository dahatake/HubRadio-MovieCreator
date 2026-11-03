#Requires -Version 7.0
# FR-139 AC-159（integration）, AC-161（manual → 文面の自動走査）。AC-160 は fr-137-139-character-fonts.ps1。
param([Parameter(Mandatory)][ValidateSet('AC-159','AC-161')][string]$Ac)
$ErrorActionPreference = 'Stop'  # dot-source の ParserError/未定義関数を終了コード 0 で終わらせない
. (Join-Path $PSScriptRoot 'StyleTemplate.common.ps1')

function Get-Luminance([string]$hex) {
    $h = $hex.TrimStart('#'); $c = 0, 2, 4 | ForEach-Object { [Convert]::ToInt32($h.Substring($_, 2), 16) / 255.0 }
    $l = $c | ForEach-Object { if ($_ -le 0.03928) { $_ / 12.92 } else { [math]::Pow(($_ + 0.055) / 1.055, 2.4) } }
    0.2126 * $l[0] + 0.7152 * $l[1] + 0.0722 * $l[2]
}
function Get-Contrast([string]$a, [string]$b) {
    $x = Get-Luminance $a; $y = Get-Luminance $b
    ([math]::Max($x, $y) + 0.05) / ([math]::Min($x, $y) + 0.05)
}

if ($Ac -eq 'AC-161') {
    # 一覧・説明・users-guide の文面に、効果の保証・断定、特定チャンネル・人物の模倣の表現がない。
    $root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
    $files = @(Get-ChildItem -LiteralPath (Join-Path $root 'users-guide') -Recurse -File -Include *.md -ErrorAction SilentlyContinue) |
        Where-Object { $_.Name -match '(演出|テンプレート|template|style)' -or (Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8) -match '演出テンプレート' }
    $src = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Recurse -File -Include *.json, *.resx, *.xaml -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '(?i)template' })
    $targets = @($files) + @($src)
    if ($targets.Count -eq 0) { Write-Error 'no template description / users-guide text found to inspect'; exit 1 }
    $bad = '(視聴維持率|再生数|再生回数|バズ|伸びる|必ず|確実に|保証|間違いなく|(?i:retention|guarantee|viral)).{0,15}(上が|アップ|増|向上|保証|確実|伸び|up|boost)|(.{0,10})(風|そっくり|と同じ|の真似|を模倣|完全再現)(の演出|チャンネル|さん)'
    $hits = foreach ($f in $targets) {
        $t = Get-Content -LiteralPath $f.FullName -Raw -Encoding UTF8
        if ($t -match $bad) { "$($f.Name): $($Matches[0])" }
    }
    if ($hits) { Write-Error ("forbidden expression: " + ($hits -join '; ')); exit 1 }
    Write-Host 'FR-139 AC-161 PASS'; exit 0
}

$sb = New-AppSandbox
$p = $null
Invoke-Test -Label "FR-139 $Ac" -Sandbox $sb -Body {
    try {
        $video = New-SyntheticVideo $sb $sb.Work
        $p = Start-App $sb; $root = Get-Root $p
        $null = New-ProjectWithCompletedJobs $root $sb 'tpl-quality' 0 $video
        # FR-139 AC-159 は 7 種（解説・ショート飛び出し・バラエティ・対談番組・縦型3段・キャラクター対話・手作り感）すべての
        # 適用成果物が対象。キャラクター対話は立ち絵が 1 件以上ないと選べない（AC-155）ので、先に登録してから作る。
        # AC-165（立ち絵の確認）は AC-159 の代用にしない: ここでは 7 種それぞれの成果物を条項別に測る。
        $img = Join-Path $sb.Work 'portrait.png'
        New-SyntheticImage $img
        Register-Portrait $root 'キャラA' $img
        $i = 0
        foreach ($n in $script:TemplateNames) {
            $i++; Select-Template $root $n; New-TemplateJob $root $i
        }
        # StyleTemplateRenderRecord（style-template-render.json）だけを読む。templateId を含むだけの別 JSON は対象外。
        $recs = @(Get-WorkspaceText $sb 'style-template-render.json' | Where-Object { $_.Text -match '(?i)templateId|template_id' })
        if ($recs.Count -lt 7) { throw "expected 7 template job records (one per template), got $($recs.Count)" }
        # 条項別の証拠: テンプレートごとに contrast / 非色依存強調 / 否定・条件語 / 下書き表示 を測った件数を残す。
        $evidence = @{}
        foreach ($r in $recs) {
            $j = $r.Text | ConvertFrom-Json -Depth 32
            $tid = if ($j.templateId) { [string]$j.templateId } elseif ($j.template_id) { [string]$j.template_id } else { $null }
            if (-not $tid) { throw "template id missing in job record: $($r.Path)" }
            $subs = @($j.subtitles); $emp = @($j.emphasis)
            if ($subs.Count -lt 1) { throw "no subtitles in the deliverable for template ${tid}: $($r.Path)" }
            foreach ($t in $subs + $emp) {
                if ((Get-Contrast $t.color $t.background) -lt 4.5) { throw "contrast below 4.5:1 ($($t.color) on $($t.background)) template ${tid}: $($r.Path)" }
            }
            foreach ($e in $emp) {
                if (-not ($e.bold -or $e.scale -gt 1 -or $e.motion)) { throw "emphasis identifiable only by color (template $tid)" }
            }
            $source = (@($j.sourceTranscript | ForEach-Object { $_.text }) -join "`n")
            $shown = (@($j.subtitles | ForEach-Object { $_.text }) -join "`n")
            foreach ($neg in 'ない', 'ず', 'ません', 'もし', 'ただし', 'でなければ', '以外') {
                if ($source.Contains($neg) -and -not $shown.Contains($neg)) { throw "negation/condition word dropped from captions (template ${tid}): $neg" }
            }
            if (-not $j.captionsDraft) { throw "auto captions not marked as draft (template $tid)" }
            $evidence[$tid] = [pscustomobject]@{ contrast = $subs.Count + $emp.Count; emphasisNonColor = $emp.Count; draft = $true }
        }
        if ($evidence.Count -lt 7) { throw "AC-159 needs evidence from all 7 templates, got $($evidence.Count): $($evidence.Keys -join ',')" }
        Write-Host ("FR-139 AC-159 evidence per template: " + (($evidence.GetEnumerator() | ForEach-Object { "$($_.Key)[contrast=$($_.Value.contrast),emphasis=$($_.Value.emphasisNonColor),draft]" }) -join '; '))
        # 人名・数字を利用者が確認・修正できる
        Invoke-Element (Find-ByName $root '字幕の確認')
        if (-not (Test-ByName $root '下書き' 10)) { throw 'draft label not shown in UI' }
        $edit = Find-EditByName $root '字幕の文'
        Set-Value $edit ((Get-Value $edit) + '')
    } finally { Stop-App $p }
}
