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
        $i = 0
        foreach ($n in '解説', 'ショート飛び出し', 'バラエティ', '対談番組', '縦型3段', '手作り感') {
            $i++; Select-Template $root $n; New-TemplateJob $root $i
        }
        $recs = @(Get-WorkspaceText $sb | Where-Object { $_.Text -match '(?i)templateId|template_id' })
        if ($recs.Count -lt 6) { throw "expected 6 template job records, got $($recs.Count)" }
        foreach ($r in $recs) {
            $j = $r.Text | ConvertFrom-Json -Depth 32
            foreach ($t in @($j.subtitles) + @($j.emphasis)) {
                if ((Get-Contrast $t.color $t.background) -lt 4.5) { throw "contrast below 4.5:1 ($($t.color) on $($t.background)): $($r.Path)" }
            }
            foreach ($e in @($j.emphasis)) {
                if (-not ($e.bold -or $e.scale -gt 1 -or $e.motion)) { throw 'emphasis identifiable only by color' }
            }
            $source = (@($j.sourceTranscript | ForEach-Object { $_.text }) -join "`n")
            $shown = (@($j.subtitles | ForEach-Object { $_.text }) -join "`n")
            foreach ($neg in 'ない', 'ず', 'ません', 'もし', 'ただし', 'でなければ', '以外') {
                if ($source.Contains($neg) -and -not $shown.Contains($neg)) { throw "negation/condition word dropped from captions: $neg" }
            }
            if (-not $j.captionsDraft) { throw 'auto captions not marked as draft' }
        }
        # 人名・数字を利用者が確認・修正できる
        Invoke-Element (Find-ByName $root '字幕の確認')
        if (-not (Test-ByName $root '下書き' 10)) { throw 'draft label not shown in UI' }
        $edit = Find-EditByName $root '字幕の文'
        Set-Value $edit ((Get-Value $edit) + '')
    } finally { Stop-App $p }
}
