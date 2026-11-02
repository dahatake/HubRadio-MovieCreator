#Requires -Version 7.0
param([string]$Distribution)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\e2e\MediaTools.ps1')
$temp = Join-Path ([IO.Path]::GetTempPath()) ('hr-media-regression-' + [guid]::NewGuid().ToString('N'))
$savedPath = $env:PATH
try {
    $app = New-Item -ItemType Directory -Path (Join-Path $temp 'app with spaces')
    $tools = New-Item -ItemType Directory -Path (Join-Path $app 'Tools')
    $pathTools = New-Item -ItemType Directory -Path (Join-Path $temp 'path tools')
    foreach ($name in 'ffmpeg', 'ffprobe') {
        $bundled = Join-Path $tools "$name.exe"
        $fallback = Join-Path $pathTools "$name.exe"
        [IO.File]::WriteAllText($bundled, 'resolution fixture; never executed')
        [IO.File]::WriteAllText($fallback, 'resolution fixture; never executed')
        $env:PATH = ''
        if ((Resolve-TestMediaTool -Name $name -AppDir $app) -ne $bundled) { throw "$name not resolved without PATH" }
        $env:PATH = [string]$pathTools
        if ((Resolve-TestMediaTool -Name $name -AppDir $app) -ne $bundled) { throw "PATH shadowed bundled $name" }
        Remove-Item -LiteralPath $bundled
        if ((Resolve-TestMediaTool -Name $name -AppDir $app) -ne $fallback) { throw "$name development fallback failed" }
        $env:PATH = ''
        $failed = $false
        try { $null = Resolve-TestMediaTool -Name $name -AppDir $app } catch {
            $failed = $_.Exception.Message.Contains("$name not available") -and $_.Exception.Message.Contains($bundled)
        }
        if (-not $failed) { throw "missing $name did not fail with actionable diagnostic" }
    }
    Write-Host 'PASS: bundled priority, empty PATH, spaces, fallback, missing tools (ffmpeg and ffprobe)'
    if ($Distribution) {
        # Exercise the actual packaged binaries, without opening the app or
        # touching its workspace-location.json. No PATH dependency.
        $env:PATH = ''
        $ffmpeg = Resolve-TestMediaTool -Name ffmpeg -AppDir $Distribution
        $ffprobe = Resolve-TestMediaTool -Name ffprobe -AppDir $Distribution
        $video = Join-Path $temp 'roundtrip.mp4'
        # This is a tool-discovery regression, not the AC-057 codec oracle.
        # The LGPL distribution intentionally need not include GPL libx264.
        & $ffmpeg -v error -y -f lavfi -i 'color=c=red:size=64x64:rate=5:duration=1' -c:v mpeg4 -pix_fmt yuv420p $video
        if ($LASTEXITCODE -ne 0) { throw "bundled ffmpeg failed: $LASTEXITCODE" }
        $json = & $ffprobe -v error -show_streams -of json $video
        if ($LASTEXITCODE -ne 0) { throw "bundled ffprobe failed: $LASTEXITCODE" }
        $stream = @((($json -join "`n") | ConvertFrom-Json).streams)[0]
        if ($stream.width -ne 64 -or $stream.height -ne 64) { throw 'roundtrip dimensions differ from synthetic fixture' }
        $frame = Join-Path $temp 'frame.png'
        & $ffmpeg -v error -y -i $video -frames:v 1 $frame
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $frame)) { throw 'bundled frame extraction failed' }
        Write-Host 'PASS: real bundled ffmpeg -> MP4 -> ffprobe -> PNG, PATH empty'
    }
} finally {
    $env:PATH = $savedPath
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
