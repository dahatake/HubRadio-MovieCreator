#Requires -Version 7.0
# FR-133 AC-142 (system): 新しく作った Project の Workspace 配下のフォルダー名が GUID ではなく Project 名と一致し、
# そのフォルダーに Project のファイルが保存され、Job の作成・実行・仕上がりの確認ができる。
. (Join-Path $PSScriptRoot 'HubRadioUia.ps1')
$sb = New-AppSandbox
$p = $null
Invoke-Test -Label 'FR-133 AC-142' -Sandbox $sb -Body {
    try {
        $video = New-SyntheticVideo $sb $sb.Work
        $ws = Join-Path $sb.Work 'ws'
        New-Item -ItemType Directory -Path $ws | Out-Null
        $name = 'hubradio-sample'
        # 設定の場所を、空の一時フォルダーにする（FR-064 の画面操作）
        $p = Start-App $sb
        $root = Get-Root $p
        Open-Settings $root
        Set-Value (Find-ByName $root 'Workspaceフォルダーの場所') $ws
        Invoke-Element (Find-ByName $root '保存')
        $null = Find-ByName $root '保存しました。アプリを再起動すると新しい場所を使います。今ある案件のファイルは自動では移動されません。'
        Stop-App $p
        $p = Start-App $sb
        $root = Get-Root $p
        New-ProjectWithCompletedJobs $root $sb $name 1 $video
        $dir = Join-Path $ws "projects\$name"
        if (-not (Test-Path -LiteralPath $dir -PathType Container)) { throw "folder '$dir' not found" }
        $guid = Get-ChildItem (Join-Path $ws 'projects') -Directory | Where-Object { $_.Name -match '^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$|^[0-9a-fA-F]{32}$' }
        if ($guid) { throw "GUID-named project folder exists: $($guid.Name)" }
        if (-not (Get-ChildItem $dir -Recurse -File | Select-Object -First 1)) { throw 'project folder has no files' }
        Invoke-Element (Find-ByName $root '仕上がり')
        $null = Find-ByName $root '完了' 30
    } finally { Stop-App $p }
}
