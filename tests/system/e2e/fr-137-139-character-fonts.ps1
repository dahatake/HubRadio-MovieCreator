#Requires -Version 7.0
# FR-137 AC-155（integration）, FR-139 AC-160（integration）。参考スクリプト: 台帳では blocked（結合試験は実装役が担当）。
param([Parameter(Mandatory)][ValidateSet('AC-155','AC-160')][string]$Ac)
$ErrorActionPreference = 'Stop'  # dot-source の ParserError/未定義関数を終了コード 0 で終わらせない
. (Join-Path $PSScriptRoot 'StyleTemplate.common.ps1')

$sb = New-AppSandbox
$p = $null
Invoke-Test -Label "FR-137/139 $Ac" -Sandbox $sb -Body {
    try {
        $root0 = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
        $video = New-SyntheticVideo $sb $sb.Work
        $p = Start-App $sb; $root = Get-Root $p
        $null = New-ProjectWithCompletedJobs $root $sb 'tpl-char' 0 $video

        if ($Ac -eq 'AC-155') {
            # 異常系: アプリは標準の立ち絵を同梱しない（画像資産がない）
            $bundled = @(Get-ChildItem -LiteralPath (Join-Path $root0 'src') -Recurse -File -Include *.png, *.webp, *.jpg -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match '(?i)(character|portrait|tachie|立ち絵)' })
            if ($bundled.Count -gt 0) { throw "bundled portrait found: $($bundled[0].FullName)" }
            # 立ち絵 0 件ではキャラクター対話を選べない
            Open-TemplateList $root
            $item = Find-ByName $root 'キャラクター対話'
            if ($item -and (Test-Enabled $item)) { throw 'character dialogue selectable with no portrait registered' }
            # 自分の画像を使用権の確認状態つきで登録 → 選べる・確認状態が記録される
            $img = Join-Path $sb.Work 'portrait.png'
            New-SyntheticImage $img
            Register-Portrait $root 'キャラA' $img
            Select-Template $root 'キャラクター対話'
            New-TemplateJob $root 1
            $recs = @(Get-WorkspaceText $sb | Where-Object { $_.Text -match '(?i)rightsConfirmed|usageRights' })
            if ($recs.Count -lt 1) { throw 'rights confirmation state not recorded' }
            $j = Get-TemplateJobRecord $sb
            if (-not $j.portraits -or -not $j.subtitles) { throw 'portrait / dialogue subtitles missing in deliverable' }
            $colors = @($j.subtitles | ForEach-Object { "$($_.speaker):$($_.color)" } | Sort-Object -Unique)
            $bySpeaker = $j.subtitles | Group-Object speaker
            if ($bySpeaker.Count -ge 2 -and (@($j.subtitles.color | Sort-Object -Unique)).Count -lt 2) { throw "subtitle colors not separated per character: $colors" }
        }
        else {
            # AC-160: 効果音は FR-060 の合成 2 種だけ／書体は同梱 3 種だけ。名前・版・ライセンスの記録
            $null = New-JobWithTemplate $root $sb 'キャラクター対話' $video 1
            $j = Get-TemplateJobRecord $sb
            $se = @($j.soundEffects | ForEach-Object { $_.kind } | Sort-Object -Unique)
            if ($se.Count -gt 2) { throw "more than 2 sound effect kinds: $se" }
            if (@($j.soundEffects | Where-Object { $_.synthetic -ne $true }).Count -gt 0) { throw 'non-synthetic sound effect used' }
            $allowed = 'Noto Sans JP Black', 'M PLUS Rounded 1c', 'Potta One'
            if (-not $j.fonts -or @($j.fonts).Count -lt 1) { throw 'font records missing' }
            foreach ($f in $j.fonts) {
                if ($f.name -notin $allowed) { throw "non-bundled / unlicensed font used: $($f.name)" }
                if (-not $f.version -or $f.license -notmatch 'OFL') { throw "font version/license not recorded: $($f.name)" }
            }
        }
    } finally { Stop-App $p }
}
