#Requires -Version 7.0
# FR-131 AC-133 AC-134 AC-135（integration）。期待値は受入基準だけから決める。
param([Parameter(Mandatory)][ValidateSet('AC-133','AC-134','AC-135')][string]$Ac)
$ErrorActionPreference = 'Stop'  # dot-source の ParserError/未定義関数を終了コード 0 で終わらせない
. (Join-Path $PSScriptRoot 'HubRadioUia.ps1')
$sb = New-AppSandbox
$p = $null
Invoke-Test -Label "FR-131 $Ac" -Sandbox $sb -Body {
    try {
        $video = New-SyntheticVideo $sb $sb.Work
        $p = Start-App $sb; $root = Get-Root $p
        New-ProjectWithCompletedJobs $root $sb 'thumb-int' 2 $video
        switch ($Ac) {
            'AC-133' {
                # サムネイルのない Job に代替表示、他 Job の表示は継続。
                Invoke-Element (Find-ByName $root 'ジョブの状態')
                $all = @(Find-AllByNameLike $root '(?i)(サムネイル|thumbnail|代替)')
                if ($all.Count -lt 2) { throw 'placeholder or thumbnails missing' }
            }
            'AC-134' {
                # 再作成・更新後に最新反映、取り違えなし。
                Invoke-Element (Find-ByName $root '仕上がり')
                $before = (Find-AllByNameLike $root '(?i)(サムネイル|thumbnail)' | ForEach-Object { $_.Current.Name }) -join '|'
                Invoke-Element (Find-ByName $root '更新')
                $after = (Find-AllByNameLike $root '(?i)(サムネイル|thumbnail)' | ForEach-Object { $_.Current.Name }) -join '|'
                if (-not $after) { throw 'thumbnails missing after refresh' }
                if (@($after -split '\|' | Sort-Object -Unique).Count -lt 2) { throw 'thumbnails confused between jobs' }
            }
            'AC-135' {
                # 代替テキストで識別、キーボードで選択可能。
                Invoke-Element (Find-ByName $root '仕上がり')
                $t = @(Find-AllByNameLike $root '(?i)(サムネイル|thumbnail)')
                if ($t.Count -lt 2) { throw 'thumbnails missing' }
                foreach ($e in $t) {
                    if ($e.Current.Name -notmatch '(Job|ジョブ|\d{4})') { throw 'no identifying text' }
                    if (-not $e.Current.IsKeyboardFocusable) { throw 'not keyboard focusable' }
                    if ($e.Current.Name -notmatch '(生成中|完了|失敗|取消)') { throw 'status not exposed in text' }
                }
            }
        }
    } finally { Stop-App $p }
}
