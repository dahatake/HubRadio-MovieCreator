#Requires -Version 7.0
# FR-132 AC-136 AC-137 (system): 仕上がり画面で書き出し先を選んで保存 → 動画とサムネイルが保存され、
# ファイル名に Project 名・Job 名（または作成日時）・種類を含む。元の成果物は不変。
# 同名ファイルがあるときは既存を変えず、連番の別名で保存する。
$ErrorActionPreference = 'Stop'  # dot-source の ParserError/未定義関数を終了コード 0 で終わらせない
. (Join-Path $PSScriptRoot 'HubRadioUia.ps1')
$sb = New-AppSandbox
$p = $null
Invoke-Test -Label 'FR-132 AC-136/AC-137' -Sandbox $sb -Body {
    try {
        $video = New-SyntheticVideo $sb $sb.Work
        $dest = Join-Path $sb.Work 'export-dest'
        New-Item -ItemType Directory -Path $dest | Out-Null
        $projectName = 'export-sample'
        $p = Start-App $sb
        $root = Get-Root $p
        New-ProjectWithCompletedJobs $root $sb $projectName 1 $video
        Invoke-Element (Find-ByName $root '仕上がり')

        # 元の成果物のハッシュ（Workspace 配下。Project 名のフォルダー）
        $ws = Get-WorkspaceDir $sb
        $originalsBefore = @(Get-ChildItem -LiteralPath $ws -Recurse -File -Include *.mp4, *.png, *.jpg -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notlike "$dest*" } | ForEach-Object { "$($_.FullName)|$((Get-FileHash $_.FullName).Hash)" } | Sort-Object)
        if ($originalsBefore.Count -eq 0) { throw 'no original deliverables found under workspace' }

        # AC-136: 書き出し先を入力して保存
        Set-Value (Find-ByName $root '書き出し先のフォルダー') $dest
        Invoke-Element (Find-ByName $root '書き出し')
        $sw = [Diagnostics.Stopwatch]::StartNew()
        while (@(Get-ChildItem $dest -File).Count -lt 4 -and $sw.Elapsed.TotalSeconds -lt 120) { Start-Sleep -Milliseconds 500 }
        $files = @(Get-ChildItem $dest -File)
        if ($files.Count -ne 4) { throw "expected 4 exported files (long, short clean, short burned, thumbnail), got $($files.Count)" }
        foreach ($f in $files) { if ($f.Name -notlike "*$projectName*") { throw "file name lacks project name: $($f.Name)" } }
        foreach ($kind in '長尺', '短尺クリーン', '短尺焼き込み', 'サムネイル') {
            if (-not ($files | Where-Object { $_.Name -like "*$kind*" })) { throw "no exported file for kind '$kind'" }
        }
        $originalsAfter = @(Get-ChildItem -LiteralPath $ws -Recurse -File -Include *.mp4, *.png, *.jpg |
            Where-Object { $_.FullName -notlike "$dest*" } | ForEach-Object { "$($_.FullName)|$((Get-FileHash $_.FullName).Hash)" } | Sort-Object)
        if (($originalsBefore -join "`n") -ne ($originalsAfter -join "`n")) { throw 'original deliverables changed by export' }

        # AC-137: 2 回目。既存の内容と更新日時は不変、新しいファイルが別名で増える
        $snapshot = $files | ForEach-Object { [pscustomobject]@{ N = $_.Name; H = (Get-FileHash $_.FullName).Hash; T = $_.LastWriteTimeUtc } }
        Start-Sleep -Seconds 2
        Invoke-Element (Find-ByName $root '書き出し')
        $sw = [Diagnostics.Stopwatch]::StartNew()
        while (@(Get-ChildItem $dest -File).Count -lt 8 -and $sw.Elapsed.TotalSeconds -lt 120) { Start-Sleep -Milliseconds 500 }
        if (@(Get-ChildItem $dest -File).Count -ne 8) { throw 'second export did not add 4 new numbered files' }
        foreach ($s in $snapshot) {
            $cur = Get-Item -LiteralPath (Join-Path $dest $s.N)
            if ((Get-FileHash $cur.FullName).Hash -ne $s.H) { throw "existing file content changed: $($s.N)" }
            if ($cur.LastWriteTimeUtc -ne $s.T) { throw "existing file timestamp changed: $($s.N)" }
        }
        $newOnes = @(Get-ChildItem $dest -File | Where-Object { $_.Name -notin $snapshot.N })
        foreach ($n in $newOnes) { if ($n.Name -notmatch '\d') { throw "new file has no sequence number: $($n.Name)" } }
    } finally { Stop-App $p }
}
