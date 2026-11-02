#Requires -Version 7.0
# Test infrastructure only: resolve the media inspector from the distribution
# being tested, not from an unrelated checkout/build. No UI or application state.
function Resolve-TestMediaTool {
    param(
        [Parameter(Mandatory)][ValidateSet('ffmpeg', 'ffprobe')][string]$Name,
        [Parameter(Mandatory)][string]$AppDir
    )
    $bundled = Join-Path $AppDir "Tools\$Name.exe"
    if (Test-Path -LiteralPath $bundled -PathType Leaf) {
        return (Resolve-Path -LiteralPath $bundled).Path
    }
    # Development builds may omit Tools. This fallback is for test fixture
    # generation/inspection only; it does not install a runtime for the app.
    $command = Get-Command "$Name.exe", $Name -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($command) { return $command.Source }
    throw "$Name not available (expected '$bundled' or an application on PATH)"
}
