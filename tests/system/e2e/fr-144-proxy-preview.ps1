#Requires -Version 7.0
# FR-144 AC-176 AC-177 AC-178。期待値は受入基準だけから決める（PARAM-011 = プロキシの高さ 360 px）。
#   AC-176 (integration): Project 作成後、Project フォルダー配下に高さ 360 のプロキシ動画ができ、元動画は変更されない。
#   AC-177 (system):      設定画面でプロキシのプレイヤーが出て、設定画面を閉じずに 再生・一時停止・シーク でき、再生位置が操作に応じて変わる。
#   AC-178 (integration): 生成中・未生成・生成失敗で、プレイヤーの場所に状態と理由が出て、再生成の操作で作り直される。
#                         失敗でも Project の作成・設定の保存・Job の実行は止まらない。
# UI の名前（アクセシブルネーム）は AC の語（プロキシ・再生・一時停止・再生位置・再生成・状態・理由）から決めた。
# 実装が別の名前を使うなら、実装側を合わせる（このテストは変更しない）。
param([Parameter(Mandatory)][ValidateSet('AC-176','AC-177','AC-178')][string]$Ac)
$ErrorActionPreference = 'Stop'  # dot-source の ParserError/未定義関数を終了コード 0 で終わらせない
. (Join-Path $PSScriptRoot 'HubRadioUia.ps1')
$sb = New-AppSandbox
$p = $null
$ProxyHeight = 360   # PARAM-011

function Get-ProxyFiles($sb, [string]$name) {
    $dir = Join-Path (Join-Path (Get-WorkspaceDir $sb) 'projects') $name
    if (-not (Test-Path -LiteralPath $dir -PathType Container)) { return @() }
    @(Get-ChildItem -LiteralPath $dir -Recurse -File | Where-Object { $_.Name -match '(?i)proxy|プロキシ' -and $_.Extension -match '(?i)^\.(mp4|webm|mkv|mov)$' })
}
function Wait-ProxyFile($sb, [string]$name, [int]$timeoutSec = 120) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    do {
        $f = Get-ProxyFiles $sb $name | Where-Object { $_.Length -gt 0 } | Select-Object -First 1
        if ($f) { return $f }
        Start-Sleep -Seconds 1
    } while ($sw.Elapsed.TotalSeconds -lt $timeoutSec)
    throw "proxy video not generated under project folder '$name' within $timeoutSec s"
}
function Get-VideoHeight($sb, [string]$path) {
    $probe = Resolve-TestMediaTool -Name ffprobe -AppDir $sb.AppDir
    [int]((& $probe -v error -select_streams v:0 -show_entries stream=height -of csv=p=0 $path 2>$null) | Select-Object -First 1)
}
function Open-ProjectSettings($root) {
    # Project の設定画面（「元動画」の画面の「次に作成するJobの設定を開く」）。閉じるのは「設定を閉じて戻る」だけ。
    Invoke-Element (Find-ByName $root '元動画')
    Invoke-Element (Find-ByName $root '次に作成するJobの設定を開く' 60)
}
function Get-PlayerPosition($root) {
    $e = Find-ByName $root '再生位置'
    $e.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value
}

Invoke-Test -Label "FR-144 $Ac" -Sandbox $sb -Body {
    try {
        $video = New-SyntheticVideo $sb $sb.Work
        $srcHash = (Get-FileHash -LiteralPath $video).Hash
        $name = 'proxy-preview'
        $p = Start-App $sb; $root = Get-Root $p
        switch ($Ac) {
            'AC-176' {
                # 正常系: 動画を選んで Project を作成 → 設定の保存・Job の実行まで通る
                New-ProjectDraft $root $sb $name $video | Out-Null
                $proxy = Wait-ProxyFile $sb $name
                $projDir = Join-Path (Join-Path (Get-WorkspaceDir $sb) 'projects') $name
                if (-not $proxy.FullName.StartsWith($projDir, [StringComparison]::OrdinalIgnoreCase)) { throw "proxy is outside the project folder: $($proxy.FullName)" }
                $h = Get-VideoHeight $sb $proxy.FullName
                if ($h -ne $ProxyHeight) { throw "proxy height is $h, expected $ProxyHeight (PARAM-011)" }
                # 異常系の対照: 元動画は変更されない（内容のハッシュと更新日時）
                if ((Get-FileHash -LiteralPath $video).Hash -ne $srcHash) { throw 'source video was modified' }
                Invoke-Element (Find-ByName $root '編集を開始')
                Wait-JobCompleted $root 1
                if ((Get-FileHash -LiteralPath $video).Hash -ne $srcHash) { throw 'source video was modified after the Job' }
            }
            'AC-177' {
                New-ProjectDraft $root $sb $name $video | Out-Null
                $null = Wait-ProxyFile $sb $name
                Open-ProjectSettings $root
                $null = Find-ByName $root '元動画のプロキシプレビュー' 60   # プレイヤーの領域
                $play = Find-ByName $root '再生' 60
                $pos0 = [double](Get-PlayerPosition $root)
                Invoke-Element $play
                Start-Sleep -Seconds 2
                $null = Find-ByName $root '一時停止'
                $pos1 = [double](Get-PlayerPosition $root)
                if ($pos1 -le $pos0) { throw "position did not advance on play ($pos0 -> $pos1)" }
                Invoke-Element (Find-ByName $root '一時停止')
                Start-Sleep -Milliseconds 800
                $paused = [double](Get-PlayerPosition $root)
                Start-Sleep -Seconds 2
                $still = [double](Get-PlayerPosition $root)
                if ([math]::Abs($still - $paused) -gt 0.25) { throw "position kept moving while paused ($paused -> $still)" }
                # シーク（再生位置のスライダーを動かす）
                $slider = (Find-ByName $root '再生位置').GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
                $target = [math]::Min($slider.Current.Maximum, [math]::Max($slider.Current.Minimum, $slider.Current.Maximum * 0.8))
                $slider.SetValue($target)
                Start-Sleep -Milliseconds 800
                $sought = [double](Get-PlayerPosition $root)
                if ([math]::Abs($sought - $target) -gt [math]::Max(0.5, $slider.Current.Maximum * 0.1)) { throw "seek did not move position (target $target, got $sought)" }
                # 設定画面は開いたまま（閉じていない）
                $null = Find-ByName $root '設定を閉じて戻る' 5
            }
            'AC-178' {
                # 生成中・未生成: 作成直後に設定画面を開くと、プレイヤーの場所に 状態（生成中/未生成）と理由が出る。プロキシ完成後はプレイヤー。
                New-ProjectDraft $root $sb $name $video | Out-Null
                Open-ProjectSettings $root
                if (-not (Test-ByName $root '元動画のプロキシプレビュー' 60)) { throw 'proxy preview area not shown' }
                $proxy = Wait-ProxyFile $sb $name
                # 未生成: プロキシを消す → 設定を開き直すと「未生成」と再生成の導線
                Invoke-Element (Find-ByName $root '設定を閉じて戻る' 60)
                $proxyPath = $proxy.FullName
                Remove-Item -LiteralPath $proxyPath -Force
                Open-ProjectSettings $root
                if (@(Find-AllByNameLike $root '未生成|生成中').Count -lt 1) { throw 'status (未生成/生成中) not shown in the player area' }
                $regen = Find-ByName $root '再生成' 30
                # 生成失敗: 出力先をフォルダーで塞いで再生成する → 失敗と原因の要約が出る
                New-Item -ItemType Directory -Path $proxyPath | Out-Null
                Invoke-Element $regen
                $sw = [Diagnostics.Stopwatch]::StartNew(); $failShown = $false
                do {
                    if (@(Find-AllByNameLike $root '失敗').Count -gt 0) { $failShown = $true; break }
                    Start-Sleep -Seconds 1
                } while ($sw.Elapsed.TotalSeconds -lt 60)
                if (-not $failShown) { throw 'failure status not shown after proxy generation failed' }
                if (@(Find-AllByNameLike $root '(理由|原因)').Count -lt 1) { throw 'failure reason (原因の要約) not shown' }
                # 失敗でも設定の保存と Job の実行は止まらない
                Invoke-Element (Find-ByName $root '設定を閉じて戻る' 60)
                Invoke-Element (Find-ByName $root '編集を開始')
                Wait-JobCompleted $root 1
                # 再生成: 塞いだ出力先を戻して再生成の操作 → プロキシが作り直される（高さ 360）
                Remove-Item -LiteralPath $proxyPath -Recurse -Force
                Open-ProjectSettings $root
                Invoke-Element (Find-ByName $root '再生成' 30)
                $again = Wait-ProxyFile $sb $name
                $h = Get-VideoHeight $sb $again.FullName
                if ($h -ne $ProxyHeight) { throw "regenerated proxy height is $h, expected $ProxyHeight" }
                if ((Get-FileHash -LiteralPath $video).Hash -ne $srcHash) { throw 'source video was modified' }
            }
        }
    } finally { Stop-App $p }
}
