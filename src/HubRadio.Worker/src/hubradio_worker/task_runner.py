"""One-shot native-host task transport (independent of the ping protocol).

stdin: one strict UTF-8 JSON document, at most 65,536 bytes, followed by EOF.
Exact envelope: {version:1, requestId:canonical UUID, method, workspaceRoot,
outputRoot, params}. Both roots exist; outputRoot is a strict workspace descendant
whose relative components include 'staging'. The native host, not web content,
supplies roots protected by managed-workspace ACLs. Symlinks/reparse points anywhere
in existing path chains, noncanonical paths, devices and nonregular files are refused.
This is not an OS sandbox against a principal allowed to replace those directories.

transcribe params: {audioPath,modelDirectory,inputAudioSha256,inputAudioByteSize,
languagePolicy}; languagePolicy is 'ja' or 'autoDetectWithJaJpFallback'.
repetition params: {transcript,modelRunId}; transcript is the exact snake_case
RepetitionTranscript DTO (revision_id, revision_digest, utterances, sentences, tokens).

Output: <requestId>.json, strict UTF-8, no BOM, <=16 MiB, never overwritten.
Envelope: {version:1,requestId,method,result}; result is dataclasses.asdict of the
real ASR result, or {suggestions:[dataclasses.asdict(candidate), ...]}.
Digest is SHA-256 of *file bytes*, not a reserialized or rounded representation.
stdout success: {version:1,status:'ok',requestId,method,sha256,byteSize} + LF.
stdout failure: {version:1,status:'error',code:<closed private code>} + LF, exit 1.
The host owns whole-process timeout/cancellation and staging reconciliation after kill.
"""

from __future__ import annotations

import contextlib
import hashlib
import json
import math
import os
import stat
import sys
import uuid
from dataclasses import asdict
from pathlib import Path
from typing import Any, BinaryIO

from hubradio_worker.tasks import repetition, transcribe

MAX_REQUEST_BYTES = 64 * 1024
MAX_RESULT_BYTES = 16 * 1024 * 1024
MAX_TEXT_BYTES = 65536
MAX_ITEMS = 100000
ERROR_CODES = frozenset({
    "task.invalid_request", "task.invalid_path", "task.input_mismatch",
    "task.output_failed", "task.result_too_large", "task.failed", "task.cancelled",
    "asr.invalid_audio", "asr.invalid_model", "asr.input_changed", "asr.model_changed",
    "asr.runtime_unavailable", "asr.processing_failed", "repetition.invalid_input",
    "repetition.input_too_large",
})


class TaskError(RuntimeError):
    def __init__(self, code: str) -> None:
        super().__init__("The local analysis task failed.")
        self.code = code if code in ERROR_CODES else "task.failed"


def _object(value: Any, keys: set[str]) -> dict[str, Any]:
    if type(value) is not dict or set(value) != keys:
        raise TaskError("task.invalid_request")
    return value


def _unique_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise TaskError("task.invalid_request")
        result[key] = value
    return result


def _invalid_constant(_: str) -> Any:
    raise TaskError("task.invalid_request")


def _validate_json(value: Any, depth: int = 0) -> None:
    if depth > 24:
        raise TaskError("task.invalid_request")
    if type(value) is str:
        if len(value.encode("utf-8", errors="strict")) > MAX_TEXT_BYTES:
            raise TaskError("task.result_too_large")
    elif type(value) is float and not math.isfinite(value):
        raise TaskError("task.invalid_request")
    elif type(value) is dict:
        for key, child in value.items():
            _validate_json(key, depth + 1)
            _validate_json(child, depth + 1)
    elif type(value) in (list, tuple):
        if len(value) > MAX_ITEMS:
            raise TaskError("task.result_too_large")
        for child in value:
            _validate_json(child, depth + 1)


def _digest(value: Any) -> str:
    if type(value) is not str or len(value) != 64 or any(c not in "0123456789abcdef" for c in value):
        raise TaskError("task.invalid_request")
    return value


def _uuid(value: Any) -> str:
    if type(value) is not str:
        raise TaskError("task.invalid_request")
    parsed = uuid.UUID(value)
    if parsed.int == 0 or str(parsed) != value:
        raise TaskError("task.invalid_request")
    return value


def require_path(value: Any, *, directory: bool) -> Path:
    if type(value) is not str or not value or value != value.strip() or "\0" in value:
        raise TaskError("task.invalid_path")
    path = Path(value)
    if not path.is_absolute() or str(path) != value or ".." in path.parts:
        raise TaskError("task.invalid_path")
    if os.name == "nt" and (
        value.startswith("\\\\") or ":" in value[2:]
        or any(part.endswith((".", " ")) for part in path.parts[1:])
    ):
        raise TaskError("task.invalid_path")
    try:
        for entry in (*reversed(path.parents), path):
            info = entry.lstat()
            if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
                raise TaskError("task.invalid_path")
            if entry != path and not stat.S_ISDIR(info.st_mode):
                raise TaskError("task.invalid_path")
        if os.path.normcase(str(path.resolve(strict=True))) != os.path.normcase(value):
            raise TaskError("task.invalid_path")
        info = path.lstat()
        if directory:
            if not stat.S_ISDIR(info.st_mode):
                raise TaskError("task.invalid_path")
        elif not stat.S_ISREG(info.st_mode) or info.st_nlink != 1:
            raise TaskError("task.invalid_path")
        return path
    except (OSError, ValueError):
        raise TaskError("task.invalid_path") from None


def _child(root: Path, path: Path) -> tuple[str, ...]:
    try:
        relative = path.relative_to(root)
        if not relative.parts:
            raise ValueError()
        return relative.parts
    except ValueError:
        raise TaskError("task.invalid_path") from None


def _verify_input(audio: Path, digest: str, byte_size: int) -> None:
    require_path(str(audio), directory=False)
    with audio.open("rb") as stream:
        info = os.fstat(stream.fileno())
        if not stat.S_ISREG(info.st_mode) or info.st_size != byte_size:
            raise TaskError("task.input_mismatch")
        observed = hashlib.file_digest(stream, "sha256").hexdigest()
    if observed != digest:
        raise TaskError("task.input_mismatch")


def _verify_model_paths(model: Path) -> None:
    require_path(str(model), directory=True)
    # Inference separately verifies the complete pinned file set and hashes. Inspect
    # every immediate entry here for generic Windows reparse tags, not just symlinks.
    # The model's ancestor chain is checked before enumeration or leaf metadata IO.
    try:
        for entry in model.iterdir():
            info = entry.lstat()
            if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
                raise TaskError("task.invalid_path")
            require_path(str(entry), directory=stat.S_ISDIR(info.st_mode))
    except OSError:
        raise TaskError("task.invalid_path") from None


def _repetition_dto(value: Any) -> repetition.RepetitionTranscript:
    source = _object(value, {"revision_id", "revision_digest", "utterances"})
    utterances = []
    for item in _array(source["utterances"]):
        utterance = _object(item, {"utterance_id", "source_span_id", "speaker_id", "sentences"})
        sentences = []
        for item in _array(utterance["sentences"]):
            sentence = _object(item, {"sentence_id", "tokens"})
            tokens = []
            for item in _array(sentence["tokens"]):
                token = _object(item, {"token_id", "text"})
                tokens.append(repetition.RepetitionToken(token["token_id"], token["text"]))
            sentences.append(repetition.RepetitionSentence(sentence["sentence_id"], tuple(tokens)))
        utterances.append(repetition.RepetitionUtterance(
            utterance["utterance_id"], utterance["source_span_id"], utterance["speaker_id"], tuple(sentences)
        ))
    return repetition.RepetitionTranscript(source["revision_id"], source["revision_digest"], tuple(utterances))


def _array(value: Any) -> list[Any]:
    if type(value) is not list:
        raise TaskError("task.invalid_request")
    return value


def _dispatch(method: str, params: Any, workspace: Path) -> dict[str, Any]:
    if method == "transcribe":
        args = _object(params, {
            "audioPath", "modelDirectory", "inputAudioSha256", "inputAudioByteSize", "languagePolicy"
        })
        policy = args["languagePolicy"]
        if type(policy) is not str or policy not in {"ja", transcribe.AUTO_LANGUAGE_POLICY}:
            raise TaskError("task.invalid_request")
        size = args["inputAudioByteSize"]
        if type(size) is not int or not 0 < size <= 2**63 - 1:
            raise TaskError("task.invalid_request")
        digest = _digest(args["inputAudioSha256"])
        audio = require_path(args["audioPath"], directory=False)
        _child(workspace, audio)
        model = require_path(args["modelDirectory"], directory=True)
        _verify_model_paths(model)
        _verify_input(audio, digest, size)
        result = transcribe.transcribe(audio, model, language_policy=policy)
        _verify_input(audio, digest, size)
        _verify_model_paths(model)
        if result.input_audio_sha256 != digest or result.input_audio_byte_size != size:
            raise TaskError("task.input_mismatch")
        # Match the native host boundary across ALL segments, not each token
        # array independently. Reject before asdict copies or publication begins.
        total_tokens = 0
        for segment in result.segments:
            total_tokens += len(segment.tokens)
            if total_tokens > MAX_ITEMS:
                raise TaskError("task.result_too_large")
        return asdict(result)
    if method == "repetition":
        args = _object(params, {"transcript", "modelRunId"})
        transcript = _repetition_dto(args["transcript"])
        candidates = repetition.find_repetition_suggestions(transcript, args["modelRunId"])
        return {"suggestions": [asdict(candidate) for candidate in candidates]}
    raise TaskError("task.invalid_request")


def _encode_result(value: Any) -> bytes:
    _validate_json(value)
    data = bytearray()
    encoder = json.JSONEncoder(ensure_ascii=False, allow_nan=False, sort_keys=True, separators=(",", ":"))
    for chunk in encoder.iterencode(value):
        encoded = chunk.encode("utf-8", errors="strict")
        if len(data) + len(encoded) > MAX_RESULT_BYTES:
            raise TaskError("task.result_too_large")
        data.extend(encoded)
    return bytes(data)


def _publish(root: Path, request_id: str, data: bytes) -> None:
    if not data or len(data) > MAX_RESULT_BYTES:
        raise TaskError("task.result_too_large")
    # Never need more path space than the final 36-character UUID + '.json'.
    # Two UUIDs made a valid native staging directory exceed Windows MAX_PATH.
    # The random temporary is still exclusively created and never retried/replaced.
    temporary = root / f".{uuid.uuid4().hex}.tmp"
    destination = root / f"{request_id}.json"
    owned = False
    try:
        require_path(str(root), directory=True)
        with temporary.open("xb") as stream:
            owned = True
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        require_path(str(root), directory=True)
        require_path(str(temporary), directory=False)
        if os.name == "nt":
            # Windows rename refuses an existing destination atomically.
            os.rename(temporary, destination)
        else:
            # POSIX rename overwrites: link is the atomic no-replace publication.
            os.link(temporary, destination, follow_symlinks=False)
            temporary.unlink()
        owned = False
    except OSError:
        raise TaskError("task.output_failed") from None
    finally:
        if owned:
            try:
                require_path(str(root), directory=True)
                require_path(str(temporary), directory=False)
                temporary.unlink()
            except (OSError, TaskError):
                # No success is reported; host reconciles its owned staging directory.
                pass


def _execute(input_stream: BinaryIO) -> dict[str, Any]:
    payload = input_stream.read(MAX_REQUEST_BYTES + 1)
    if not payload or len(payload) > MAX_REQUEST_BYTES or payload.startswith(b"\xef\xbb\xbf"):
        raise TaskError("task.invalid_request")
    request = json.loads(payload.decode("utf-8", errors="strict"),
                         object_pairs_hook=_unique_object, parse_constant=_invalid_constant)
    _validate_json(request)
    request = _object(request, {"version", "requestId", "method", "workspaceRoot", "outputRoot", "params"})
    if type(request["version"]) is not int or request["version"] != 1:
        raise TaskError("task.invalid_request")
    method = request["method"]
    if type(method) is not str or method not in {"transcribe", "repetition"}:
        raise TaskError("task.invalid_request")
    request_id = _uuid(request["requestId"])
    workspace = require_path(request["workspaceRoot"], directory=True)
    output = require_path(request["outputRoot"], directory=True)
    if "staging" not in _child(workspace, output):
        raise TaskError("task.invalid_path")
    # lexists also refuses dangling links and directory/file collisions before inference.
    if os.path.lexists(output / f"{request_id}.json"):
        raise TaskError("task.output_failed")
    result = _dispatch(method, request["params"], workspace)
    data = _encode_result({"version": 1, "requestId": request_id, "method": method, "result": result})
    _publish(output, request_id, data)
    return {"version": 1, "status": "ok", "requestId": request_id, "method": method,
            "sha256": hashlib.sha256(data).hexdigest(), "byteSize": len(data)}


def run_task(input_stream: BinaryIO, output_stream: BinaryIO) -> int:
    try:
        # Library-level guard; CLI additionally redirects the OS descriptors, covering
        # native ctranslate2/ffmpeg output and logging handlers bound before this scope.
        with open(os.devnull, "w", encoding="utf-8") as sink:
            with contextlib.redirect_stdout(sink), contextlib.redirect_stderr(sink):
                metadata = _execute(input_stream)
        exit_code = 0
    except (TaskError, transcribe.AsrTranscriptionError, repetition.RepetitionTaskError) as error:
        metadata = {"version": 1, "status": "error", "code": error.code if error.code in ERROR_CODES else "task.failed"}
        exit_code = 1
    except (ValueError, TypeError, UnicodeError, RecursionError):
        metadata = {"version": 1, "status": "error", "code": "task.invalid_request"}
        exit_code = 1
    except KeyboardInterrupt:
        metadata = {"version": 1, "status": "error", "code": "task.cancelled"}
        exit_code = 1
    except Exception:
        metadata = {"version": 1, "status": "error", "code": "task.failed"}
        exit_code = 1
    output_stream.write(json.dumps(metadata, sort_keys=True, separators=(",", ":")).encode("ascii") + b"\n")
    output_stream.flush()
    return exit_code


def run_task_cli() -> int:
    # Keep the metadata descriptor private; fd 1/2 stay muted through interpreter
    # teardown, so native/background logs can never become protocol output.
    with os.fdopen(os.dup(1), "wb") as metadata:
        with open(os.devnull, "wb") as sink:
            os.dup2(sink.fileno(), 1)
            os.dup2(sink.fileno(), 2)
        return run_task(sys.stdin.buffer, metadata)