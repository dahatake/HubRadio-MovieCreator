#Requires -Version 7.0
# FR-131 AC-132 (system): Job が複数ある Project で、Job を表示・選択する各画面に Job ごとのサムネイルが
# Job の名前または作成日時とともに表示され、どの Job か識別できる。
. (Join-Path $PSScriptRoot 'HubRadioUia.ps1')
$sb = New-AppSandbox
$p = $null
Invoke-Test -Label 'FR-131 AC-132' -Sandbox $sb -Body {
    try {
        $video = New-SyntheticVideo $sb $sb.Work
        # 動画処理ツール（Tools\ffmpeg.exe・ffprobe.exe）は配布物に同梱される。無い開発ビルドでは、PATH のものを一時コピーに置く
        $tools = Join-Path $sb.AppDir 'Tools'
        New-Item -ItemType Directory -Path $tools -Force | Out-Null
        foreach ($n in 'ffmpeg', 'ffprobe') {
            if (-not (Test-Path (Join-Path $tools "$n.exe"))) { $c = Get-Command $n -ErrorAction SilentlyContinue; if ($c) { Copy-Item $c.Source (Join-Path $tools "$n.exe") } }
        }
        $p = Start-App $sb
        $root = Get-Root $p
        New-ProjectWithCompletedJobs $root $sb 'thumb-sample' 2 $video
        # Job を表示・選択する画面（ジョブの状態の確認・元動画の画面の Job 一覧・仕上がり）
        foreach ($screen in 'ジョブの状態の確認', '元動画', '仕上がり') {
            Invoke-Element (Find-ByName $root $screen)
            Start-Sleep -Seconds 2
            # サムネイルは画像として「サムネイル: Job n、作成日時 …、状態」という名前で公開される
            $thumbs = @(Find-AllByNameLike $root '^サムネイル: Job \d+')
            if ($thumbs.Count -lt 2) { throw "[$screen] expected >=2 job thumbnails, found $($thumbs.Count)" }
            foreach ($t in $thumbs) {
                if ($t.Current.Name -notmatch '^サムネイル: Job \d+、作成日時 \S+') { throw "[$screen] thumbnail lacks job name/created time: '$($t.Current.Name)'" }
            }
            $names = $thumbs | ForEach-Object { $_.Current.Name } | Sort-Object -Unique
            if ($names.Count -lt 2) { throw "[$screen] thumbnails are not distinguishable per job" }
        }
    } finally { Stop-App $p }
}
