#Requires -Version 7.0
# FR-131 AC-132 (system): Job が複数ある Project で、Job を表示・選択する各画面に Job ごとのサムネイルが
# Job の名前または作成日時とともに表示され、どの Job か識別できる。
. (Join-Path $PSScriptRoot 'HubRadioUia.ps1')
$sb = New-AppSandbox
$p = $null
Invoke-Test -Label 'FR-131 AC-132' -Sandbox $sb -Body {
    try {
        $video = New-SyntheticVideo $sb $sb.Work
        $p = Start-App $sb
        $root = Get-Root $p
        New-ProjectWithCompletedJobs $root $sb 'thumb-sample' 2 $video
        foreach ($screen in 'Project 一覧', 'ジョブの状態', '仕上がり', '仕上がりの比較') {
            Invoke-Element (Find-ByName $root $screen)
            # サムネイルは画像要素として、Job の名前または作成日時を含む名前で公開される
            $thumbs = @(Find-AllByNameLike $root '(?i)(サムネイル|thumbnail)')
            if ($thumbs.Count -lt 2) { throw "[$screen] expected >=2 job thumbnails, found $($thumbs.Count)" }
            foreach ($t in $thumbs) {
                if ($t.Current.Name -notmatch '(Job|ジョブ|\d{4}[-/年]\d{1,2})') { throw "[$screen] thumbnail lacks job name/created time: '$($t.Current.Name)'" }
            }
            $names = $thumbs | ForEach-Object { $_.Current.Name } | Sort-Object -Unique
            if ($names.Count -lt 2) { throw "[$screen] thumbnails are not distinguishable per job" }
        }
    } finally { Stop-App $p }
}
