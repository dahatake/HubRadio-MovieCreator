"""Location/parser regressions, not additional acceptance cases.

FR-031 AC-079: an output manifest is evidence, not a mandated filename.
FR-063 AC-038: selection must be committed before observing column count.
"""
import os
import json
import subprocess
from pathlib import Path


ROOT = Path(__file__).parent


def test_manifest_discovery_accepts_published_guid_names(tmp_path):
    evidence = tmp_path / "evidence" / "Manifest"
    evidence.mkdir(parents=True)
    published = evidence / "00000000-0000-0000-0000-000000000001.json"
    published.write_text('{"outputs":[{"mediaType":"video/mp4"}]}')
    legacy = tmp_path / "manifest-output.json"
    legacy.write_text('{"outputs":[{}]}')
    (tmp_path / "settings.json").write_text("{}")
    (tmp_path / "result-receipt.json").write_text("{}")
    helper = ROOT / "e2e" / "OutputArtifacts.ps1"
    environment = {**os.environ, "HR_TEST_HELPER": str(helper), "HR_TEST_WS": str(tmp_path)}
    result = subprocess.run(
        ["pwsh", "-NoProfile", "-Command",
         ". $env:HR_TEST_HELPER; Find-TestRenderManifests -Workspace $env:HR_TEST_WS | ForEach-Object FullName"],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
        env=environment, timeout=30,
    )
    assert result.returncode == 0, result.stdout + result.stderr
    assert set(result.stdout.splitlines()) == {str(published), str(legacy)}


def test_all_system_powershell_scripts_parse():
    environment = {**os.environ, "HR_TEST_ROOT": str(ROOT)}
    result = subprocess.run(
        ["pwsh", "-NoProfile", "-Command",
         "$bad = @(); Get-ChildItem -LiteralPath $env:HR_TEST_ROOT -Recurse -Filter *.ps1 | "
         "Where-Object { $_.FullName -notmatch '[\\\\/](bin|obj)[\\\\/]' } | ForEach-Object { "
         "$t=$null; $e=$null; $null=[Management.Automation.Language.Parser]::ParseFile($_.FullName,[ref]$t,[ref]$e); "
         "if($e.Count) { $bad += $e; Write-Output $_.FullName; Write-Output $e } }; "
         "if($bad.Count) { exit 1 }; Write-Output 'all system PowerShell parsed'"],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
        env=environment, timeout=30,
    )
    assert result.returncode == 0, result.stdout + result.stderr
    assert "all system PowerShell parsed" in result.stdout


def test_compare_count_selection_commits_before_assertion():
    # Guard the specific harness regression: DOWN alone only navigates an
    # expanded HTML select. Desktop behavior remains for the serial UI run.
    script = (ROOT / "e2e" / "chk10-project-ui.ps1").read_text(encoding="utf-8-sig")
    branch = script.split("'AC-038' {", 1)[1]
    assert "foreach ($want in 2, 3, 4)" in branch
    assert "'{ENTER}{TAB}'" in branch
    assert "after committed selection" in branch
    assert "comparison did not retain the 4-Job limit" in branch


def test_native_permission_cases_use_the_serial_build_boundary():
    """Different native filters still share WinUI generated XAML files."""
    repo = ROOT.parents[1]
    config = json.loads((repo / "scripts" / "ebak.config.json").read_text(encoding="utf-8-sig"))
    ledger = json.loads((ROOT / "ledger.json").read_text(encoding="utf-8-sig"))
    patterns = config["system_test"]["serial_patterns"]
    cases = {case["id"]: case for case in ledger["cases"]}
    for case_id in ("IT-004", "E2E-008"):
        assert any(pattern in cases[case_id]["command"] for pattern in patterns)
    active_browse = [
        case["id"] for case in ledger["cases"]
        if case["status"] != "blocked" and "AC-041" in case["ac_ids"]
    ]
    assert active_browse == ["IT-004"], "extend the original case instead of duplicating its AC"
