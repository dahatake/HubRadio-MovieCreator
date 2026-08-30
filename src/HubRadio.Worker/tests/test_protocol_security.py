from __future__ import annotations

import json
import subprocess
import sys

import pytest  # pyright: ignore[reportMissingImports] - declared test extra; worker venv verified.

from hubradio_worker.protocol import MAX_MESSAGE_UTF8_BYTES, UNKNOWN_METHOD

REQUEST_ID = "123e4567-e89b-12d3-a456-426614174000"


def _request(method: str) -> bytes:
    value = json.dumps(
        {
            "version": 1,
            "type": "request",
            "requestId": REQUEST_ID,
            "method": method,
            "params": {},
        },
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8") + b"\n"
    assert len(value) - 1 <= MAX_MESSAGE_UTF8_BYTES
    return value


def _unknown_method_error() -> bytes:
    return json.dumps(
        {
            "version": 1,
            "type": "error",
            "requestId": REQUEST_ID,
            "error": {"code": UNKNOWN_METHOD},
        },
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8") + b"\n"


@pytest.mark.parametrize(
    "method",
    [
        "<script>x</script>",
        "worker.ping;calc",
        "worker.p\u0456ng",
        "-vf=movie=attack",
    ],
)
def test_adversarial_unknown_rpc_is_rejected_without_output_reflection(
    method: str,
) -> None:
    result = subprocess.run(
        [sys.executable, "-m", "hubradio_worker", "--protocol"],
        input=_request(method),
        check=False,
        capture_output=True,
        timeout=10,
    )

    encoded_method = method.encode("utf-8")
    assert result.returncode == 1
    assert result.stdout == _unknown_method_error()
    assert result.stderr == b""
    assert encoded_method not in result.stdout + result.stderr


@pytest.mark.parametrize(
    "unknown_arguments",
    [
        ("--unknown-command",),
        ("--heal",),
        ("--prot",),
        ("--filter", "scale=1:1"),
        ("-vf", "movie=attack"),
    ],
)
def test_unknown_cli_and_filter_options_are_rejected(
    unknown_arguments: tuple[str, ...],
) -> None:
    result = subprocess.run(
        [
            sys.executable,
            "-m",
            "hubradio_worker",
            "--protocol",
            *unknown_arguments,
        ],
        input=b"",
        check=False,
        capture_output=True,
        timeout=10,
    )

    assert result.returncode == 2
    assert result.stdout == b""
    assert b"usage:" in result.stderr.lower()
    assert b"Traceback" not in result.stderr