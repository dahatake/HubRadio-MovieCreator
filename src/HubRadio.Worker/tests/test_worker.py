from __future__ import annotations

import subprocess
import sys


def test_package_imports_without_models() -> None:
    import hubradio_worker

    assert hubradio_worker.__name__ == "hubradio_worker"


def test_health_command_returns_deterministic_json() -> None:
    result = subprocess.run(
        [sys.executable, "-m", "hubradio_worker", "--health"],
        check=False,
        capture_output=True,
        text=True,
        timeout=10,
    )

    assert result.returncode == 0
    assert result.stdout == '{"status":"ok"}\n'
    assert result.stderr == ""


def test_worker_rejects_missing_or_unknown_command() -> None:
    for arguments in ([], ["--unknown"]):
        result = subprocess.run(
            [sys.executable, "-m", "hubradio_worker", *arguments],
            check=False,
            capture_output=True,
            text=True,
            timeout=10,
        )

        assert result.returncode == 2
        assert result.stdout == ""
        assert "usage:" in result.stderr
