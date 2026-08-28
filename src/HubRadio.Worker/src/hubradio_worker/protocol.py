from __future__ import annotations

import codecs
import json
import uuid
from typing import BinaryIO, TypeGuard

PROTOCOL_VERSION = 1
MAX_MESSAGE_UTF8_BYTES = 512
PING_METHOD = "worker.ping"
FALLBACK_REQUEST_ID = "00000000-0000-0000-0000-000000000001"

INVALID_MESSAGE = "worker.invalid_message"
MESSAGE_TOO_LARGE = "worker.message_too_large"
UNKNOWN_METHOD = "worker.unknown_method"
PROCESSING_FAILED = "worker.processing_failed"
ERROR_CODES = (
    INVALID_MESSAGE,
    MESSAGE_TOO_LARGE,
    UNKNOWN_METHOD,
    PROCESSING_FAILED,
)

_REQUEST_KEYS = {"version", "type", "requestId", "method", "params"}


class _DuplicatePropertyError(ValueError):
    pass


class _ObjectPairs(list[tuple[str, object]]):
    pass


class _ProtocolViolation(Exception):
    def __init__(self, code: str, request_id: str) -> None:
        super().__init__()
        self.code = code
        self.request_id = request_id


def _reject_duplicate_properties(pairs: list[tuple[str, object]]) -> dict[str, object]:
    result: dict[str, object] = {}
    for key, value in pairs:
        if key in result:
            raise _DuplicatePropertyError()
        result[key] = value
    return result


def _reject_nonstandard_constant(_: str) -> object:
    raise ValueError()


def _retain_object_pairs(pairs: list[tuple[str, object]]) -> _ObjectPairs:
    return _ObjectPairs(pairs)


def _is_valid_request_id(value: object) -> TypeGuard[str]:
    if type(value) is not str:
        return False

    try:
        parsed = uuid.UUID(value)
    except (ValueError, AttributeError):
        return False

    return parsed.int != 0 and str(parsed) == value


def _recover_request_id(value: object) -> str:
    if type(value) is dict:
        request_id = value.get("requestId")
        if _is_valid_request_id(request_id):
            return request_id
    return FALLBACK_REQUEST_ID


def _recover_request_id_from_duplicate_json(text: str) -> str:
    try:
        value = json.loads(
            text,
            object_pairs_hook=_retain_object_pairs,
            parse_constant=_reject_nonstandard_constant,
        )
    except (json.JSONDecodeError, ValueError):
        return FALLBACK_REQUEST_ID

    if type(value) is not _ObjectPairs:
        return FALLBACK_REQUEST_ID

    candidates = [item for key, item in value if key == "requestId"]
    if len(candidates) == 1 and _is_valid_request_id(candidates[0]):
        return candidates[0]
    return FALLBACK_REQUEST_ID


def _decode_and_parse(raw_message: bytes) -> tuple[object, str]:
    if raw_message.startswith(codecs.BOM_UTF8):
        raise _ProtocolViolation(INVALID_MESSAGE, FALLBACK_REQUEST_ID)

    try:
        text = raw_message.decode("utf-8", errors="strict")
    except UnicodeDecodeError:
        raise _ProtocolViolation(INVALID_MESSAGE, FALLBACK_REQUEST_ID) from None

    try:
        value = json.loads(
            text,
            object_pairs_hook=_reject_duplicate_properties,
            parse_constant=_reject_nonstandard_constant,
        )
    except _DuplicatePropertyError:
        request_id = _recover_request_id_from_duplicate_json(text)
        raise _ProtocolViolation(INVALID_MESSAGE, request_id) from None
    except (json.JSONDecodeError, ValueError):
        raise _ProtocolViolation(INVALID_MESSAGE, FALLBACK_REQUEST_ID) from None

    return value, _recover_request_id(value)


def _read_request(input_stream: BinaryIO) -> tuple[str, str]:
    raw_line = input_stream.readline(MAX_MESSAGE_UTF8_BYTES + 1)
    if raw_line == b"":
        raise _ProtocolViolation(INVALID_MESSAGE, FALLBACK_REQUEST_ID)
    if type(raw_line) is not bytes:
        raise _ProtocolViolation(INVALID_MESSAGE, FALLBACK_REQUEST_ID)

    has_lf = raw_line.endswith(b"\n")
    raw_message = raw_line[:-1] if has_lf else raw_line
    if len(raw_message) > MAX_MESSAGE_UTF8_BYTES:
        raise _ProtocolViolation(MESSAGE_TOO_LARGE, FALLBACK_REQUEST_ID)

    value, request_id = _decode_and_parse(raw_message)
    if not has_lf or b"\r" in raw_message:
        raise _ProtocolViolation(INVALID_MESSAGE, request_id)

    if type(value) is not dict or set(value) != _REQUEST_KEYS:
        raise _ProtocolViolation(INVALID_MESSAGE, request_id)
    if type(value["version"]) is not int or value["version"] != PROTOCOL_VERSION:
        raise _ProtocolViolation(INVALID_MESSAGE, request_id)
    if type(value["type"]) is not str or value["type"] != "request":
        raise _ProtocolViolation(INVALID_MESSAGE, request_id)
    if not _is_valid_request_id(value["requestId"]):
        raise _ProtocolViolation(INVALID_MESSAGE, FALLBACK_REQUEST_ID)
    if type(value["method"]) is not str:
        raise _ProtocolViolation(INVALID_MESSAGE, request_id)
    if type(value["params"]) is not dict or value["params"]:
        raise _ProtocolViolation(INVALID_MESSAGE, request_id)

    extra = input_stream.read(1)
    if extra != b"":
        raise _ProtocolViolation(INVALID_MESSAGE, request_id)

    return request_id, value["method"]


def _write_message(output_stream: BinaryIO, message: dict[str, object]) -> bool:
    try:
        encoded = json.dumps(
            message,
            ensure_ascii=False,
            allow_nan=False,
            sort_keys=True,
            separators=(",", ":"),
        ).encode("utf-8", errors="strict")
        if len(encoded) > MAX_MESSAGE_UTF8_BYTES:
            return False
        record = encoded + b"\n"
        if output_stream.write(record) != len(record):
            return False
        output_stream.flush()
        return True
    except Exception:  # noqa: BLE001 - protocol output failures must not escape to stderr.
        return False


def _write_error(output_stream: BinaryIO, request_id: str, code: str) -> bool:
    return _write_message(
        output_stream,
        {
            "version": PROTOCOL_VERSION,
            "type": "error",
            "requestId": request_id,
            "error": {"code": code},
        },
    )


def run_protocol(input_stream: BinaryIO, output_stream: BinaryIO) -> int:
    request_id = FALLBACK_REQUEST_ID
    try:
        request_id, method = _read_request(input_stream)
        if method != PING_METHOD:
            _write_error(output_stream, request_id, UNKNOWN_METHOD)
            return 1

        progress_messages = (
            {
                "version": PROTOCOL_VERSION,
                "type": "progress",
                "requestId": request_id,
                "sequence": 1,
                "completedUnits": 0,
                "totalUnits": 1,
                "phase": PING_METHOD,
            },
            {
                "version": PROTOCOL_VERSION,
                "type": "progress",
                "requestId": request_id,
                "sequence": 2,
                "completedUnits": 1,
                "totalUnits": 1,
                "phase": PING_METHOD,
            },
        )
        for progress in progress_messages:
            if not _write_message(output_stream, progress):
                return 1

        if not _write_message(
            output_stream,
            {
                "version": PROTOCOL_VERSION,
                "type": "response",
                "requestId": request_id,
                "method": PING_METHOD,
                "result": {"protocolVersion": PROTOCOL_VERSION},
            },
        ):
            return 1
        return 0
    except _ProtocolViolation as violation:
        _write_error(output_stream, violation.request_id, violation.code)
        return 1
    except Exception:  # noqa: BLE001 - this process boundary must emit only a fixed error.
        _write_error(output_stream, request_id, PROCESSING_FAILED)
        return 1
