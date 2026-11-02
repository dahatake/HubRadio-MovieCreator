"""Regression for inspectors used by FR-008/009/010 AC-001/002/057 and FR-141 AC-169.

Infrastructure tests, not an additional ledger case for those ACs.
"""
import subprocess
from pathlib import Path


def test_media_tool_resolution():
    script = Path(__file__).parent / "tooling" / "media-tools-regression.ps1"
    result = subprocess.run(
        ["pwsh", "-NoProfile", "-File", str(script)],
        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=60,
    )
    assert result.returncode == 0, result.stdout + result.stderr
    assert "PASS: bundled priority" in result.stdout


def test_inspectors_share_the_bundled_resolver():
    """Prevent reintroducing the PATH-only probe/frame inspection failure."""
    root = Path(__file__).parent / "e2e"
    media = (root / "chk10-media-output.ps1").read_text(encoding="utf-8-sig")
    frame = (root / "fr-140-143-system.ps1").read_text(encoding="utf-8-sig")
    helper = (root / "HubRadioUia.ps1").read_text(encoding="utf-8-sig")
    assert "Resolve-TestMediaTool -Name ffprobe -AppDir $sb.AppDir" in media
    assert "Resolve-TestMediaTool -Name ffmpeg -AppDir $sb.AppDir" in frame
    assert "Resolve-TestMediaTool -Name ffprobe -AppDir $sb.AppDir" in frame
    assert "'MediaTools.ps1'" in helper
    for text in (media, frame, helper):
        assert "Get-Command ffprobe" not in text
        assert "Get-Command ffmpeg" not in text
