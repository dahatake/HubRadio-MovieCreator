from __future__ import annotations

import io
import json
import subprocess
import sys

import pytest  # pyright: ignore[reportMissingImports] - declared test extra; worker venv verified.

from hubradio_worker.protocol import (
    FALLBACK_REQUEST_ID,
    INVALID_MESSAGE,
    MAX_MESSAGE_UTF8_BYTES,
    MESSAGE_TOO_LARGE,
    PING_METHOD,
    PROCESSING_FAILED,
    UNKNOWN_METHOD,
    run_protocol,
)

REQUEST_ID = "123e4567-e89b-12d3-a456-426614174000"


def _request(
    *,
    request_id: str = REQUEST_ID,
    method: object = PING_METHOD,
    version: object = 1,
    message_type: object = "request",
    params: object | None = None,
    extra: dict[str, object] | None = None,
) -> bytes:
    message: dict[str, object] = {
        "version": version,
        "type": message_type,
        "requestId": request_id,
        "method": method,
        "params": {} if params is None else params,
    }
    if extra is not None:
        message.update(extra)
    return json.dumps(message, sort_keys=True, separators=(",", ":")).encode("utf-8") + b"\n"


def _run(value: bytes) -> tuple[int, bytes]:
    output = io.BytesIO()
    exit_code = run_protocol(io.BytesIO(value), output)
    return exit_code, output.getvalue()


def _error(code: str, request_id: str = FALLBACK_REQUEST_ID) -> bytes:
    return (
        json.dumps(
            {
                "version": 1,
                "type": "error",
                "requestId": request_id,
                "error": {"code": code},
            },
            sort_keys=True,
            separators=(",", ":"),
        ).encode("utf-8")
        + b"\n"
    )


def test_valid_ping_returns_deterministic_canonical_progress_and_response() -> None:
    exit_code, output = _run(_request())

    assert exit_code == 0
    assert output == (
        b'{"completedUnits":0,"phase":"worker.ping","requestId":"123e4567-e89b-12d3-a456-426614174000","sequence":1,"totalUnits":1,"type":"progress","version":1}\n'
        b'{"completedUnits":1,"phase":"worker.ping","requestId":"123e4567-e89b-12d3-a456-426614174000","sequence":2,"totalUnits":1,"type":"progress","version":1}\n'
        b'{"method":"worker.ping","requestId":"123e4567-e89b-12d3-a456-426614174000","result":{"protocolVersion":1},"type":"response","version":1}\n'
    )
    for line in output.splitlines():
        assert len(line) <= MAX_MESSAGE_UTF8_BYTES


def test_exact_512_byte_json_record_is_accepted() -> None:
    base = _request()[:-1]
    assert len(base) < MAX_MESSAGE_UTF8_BYTES
    boundary_record = base + (b" " * (MAX_MESSAGE_UTF8_BYTES - len(base))) + b"\n"

    exit_code, output = _run(boundary_record)

    assert exit_code == 0
    assert output.endswith(b'"type":"response","version":1}\n')


def test_513_byte_json_record_is_rejected_before_unbounded_read() -> None:
    base = _request()[:-1]
    oversized = base + (b" " * (MAX_MESSAGE_UTF8_BYTES + 1 - len(base))) + b"\n"

    exit_code, output = _run(oversized)

    assert exit_code != 0
    assert output == _error(MESSAGE_TOO_LARGE)


def test_unknown_method_in_otherwise_exact_request_preserves_request_id() -> None:
    exit_code, output = _run(_request(method="worker.future"))

    assert exit_code != 0
    assert output == _error(UNKNOWN_METHOD, REQUEST_ID)


@pytest.mark.parametrize(
    "invalid",
    [
        b"",
        b"{\n",
        b"[]\n",
        b"null\n",
        b"NaN\n",
        b"\xff\n",
        b"\xef\xbb\xbf{}\n",
        b'{"method":"worker.ping","params":{},"requestId":"123e4567-e89b-12d3-a456-426614174000","requestId":"123e4567-e89b-12d3-a456-426614174001","type":"request","version":1}\n',
    ],
)
def test_malformed_duplicate_invalid_utf8_bom_and_nonobject_inputs_fail_closed(
    invalid: bytes,
) -> None:
    exit_code, output = _run(invalid)

    assert exit_code != 0
    assert output == _error(INVALID_MESSAGE)


def test_nested_duplicate_preserves_unique_safe_root_request_id() -> None:
    invalid = b'{"method":"worker.ping","params":{"x":1,"x":2},"requestId":"123e4567-e89b-12d3-a456-426614174000","type":"request","version":1}\n'

    exit_code, output = _run(invalid)

    assert exit_code != 0
    assert output == _error(INVALID_MESSAGE, REQUEST_ID)


@pytest.mark.parametrize(
    "invalid",
    [
        _request(extra={"unexpected": 1}),
        _request(version=2),
        _request(version=True),
        _request(message_type="response"),
        _request(method=1),
        _request(params=[]),
        _request(params={"unexpected": 1}),
    ],
)
def test_schema_violations_with_safe_request_id_preserve_it(invalid: bytes) -> None:
    exit_code, output = _run(invalid)

    assert exit_code != 0
    assert output == _error(INVALID_MESSAGE, REQUEST_ID)


@pytest.mark.parametrize("missing_key", ["version", "type", "method", "params"])
def test_missing_required_key_with_safe_request_id_preserves_it(missing_key: str) -> None:
    message = json.loads(_request())
    del message[missing_key]
    invalid = json.dumps(message, sort_keys=True, separators=(",", ":")).encode("utf-8") + b"\n"

    exit_code, output = _run(invalid)

    assert exit_code != 0
    assert output == _error(INVALID_MESSAGE, REQUEST_ID)


def test_missing_request_id_uses_deterministic_fallback() -> None:
    message = json.loads(_request())
    del message["requestId"]
    invalid = json.dumps(message, sort_keys=True, separators=(",", ":")).encode("utf-8") + b"\n"

    exit_code, output = _run(invalid)

    assert exit_code != 0
    assert output == _error(INVALID_MESSAGE)


@pytest.mark.parametrize(
    "request_id",
    [
        "00000000-0000-0000-0000-000000000000",
        "123E4567-E89B-12D3-A456-426614174000",
        "not-a-uuid",
    ],
)
def test_invalid_request_ids_use_deterministic_fallback(request_id: str) -> None:
    exit_code, output = _run(_request(request_id=request_id))

    assert exit_code != 0
    assert output == _error(INVALID_MESSAGE)


def test_crlf_is_rejected_but_safe_request_id_is_preserved() -> None:
    exit_code, output = _run(_request()[:-1] + b"\r\n")

    assert exit_code != 0
    assert output == _error(INVALID_MESSAGE, REQUEST_ID)


def test_complete_json_without_lf_is_rejected_with_recovered_request_id() -> None:
    exit_code, output = _run(_request()[:-1])

    assert exit_code != 0
    assert output == _error(INVALID_MESSAGE, REQUEST_ID)


def test_partial_eof_is_rejected_with_fallback_request_id() -> None:
    exit_code, output = _run(_request()[:-20])

    assert exit_code != 0
    assert output == _error(INVALID_MESSAGE)


def test_oversized_record_without_newline_is_rejected() -> None:
    exit_code, output = _run(b"{" + (b"x" * MAX_MESSAGE_UTF8_BYTES))

    assert exit_code != 0
    assert output == _error(MESSAGE_TOO_LARGE)


@pytest.mark.parametrize("suffix", [b"\n", _request()])
def test_any_bytes_after_first_request_reject_without_success(suffix: bytes) -> None:
    exit_code, output = _run(_request() + suffix)

    assert exit_code != 0
    assert output == _error(INVALID_MESSAGE, REQUEST_ID)
    assert b'"type":"progress"' not in output
    assert b'"type":"response"' not in output


def test_invalid_input_content_is_never_reflected_in_error() -> None:
    secret = b"do-not-leak-this-input"
    exit_code, output = _run(b'{"secret":"' + secret + b'"}\n')

    assert exit_code != 0
    assert output == _error(INVALID_MESSAGE)
    assert secret not in output


def test_unexpected_input_failure_emits_fixed_processing_error_without_exception_text() -> None:
    class FailingInput:
        def readline(self, _: int) -> bytes:
            raise OSError("private input failure")

    output = io.BytesIO()

    exit_code = run_protocol(FailingInput(), output)  # type: ignore[arg-type]

    assert exit_code != 0
    assert output.getvalue() == _error(PROCESSING_FAILED)
    assert b"private input failure" not in output.getvalue()


@pytest.mark.parametrize(
    ("input_bytes", "expected_exit"),
    [
        (_request(), 0),
        (_request(method="worker.future"), 1),
        (b"not-json\n", 1),
    ],
)
def test_protocol_subprocess_has_deterministic_exit_and_empty_stderr(
    input_bytes: bytes,
    expected_exit: int,
) -> None:
    result = subprocess.run(
        [sys.executable, "-m", "hubradio_worker", "--protocol"],
        input=input_bytes,
        check=False,
        capture_output=True,
        timeout=10,
    )

    assert result.returncode == expected_exit
    assert result.stdout.endswith(b"\n")
    assert result.stderr == b""
