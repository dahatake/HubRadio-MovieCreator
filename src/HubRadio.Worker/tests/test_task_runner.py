from __future__ import annotations

import hashlib
import io
import json
import math
import os
import stat
import subprocess
import sys
import uuid
from dataclasses import replace
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import Mock

import pytest

from hubradio_worker import task_runner as runner
from hubradio_worker.tasks import transcribe as asr


@pytest.fixture
def task(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> tuple[dict, Mock]:
    workspace = tmp_path / "workspace"
    output = workspace / "staging" / "attempt"
    output.mkdir(parents=True)
    audio = workspace / "audio.wav"
    audio.write_bytes(b"unit-only audio fixture")
    model = tmp_path / "model"
    model.mkdir()
    digest = hashlib.sha256(audio.read_bytes()).hexdigest()
    result = asr.AsrTranscriptionResult(
        asr.MODEL_REPOSITORY, asr.MODEL_REVISION, asr.MODEL_MANIFEST_SHA256,
        asr.PINNED_CONFIGURATION, digest, audio.stat().st_size, 1.0, "ja", 1.0, 1.0,
        (asr.AsrSegment(7, 19, " private 音声", 0.1, 0.8, -0.2, 0.01,
                        (asr.AsrToken(" private 音声", 0.1, 0.8, 0.9, False),)),),
    )
    operation = Mock(return_value=result)
    monkeypatch.setattr(asr, "transcribe", operation)
    return {
        "version": 1, "requestId": str(uuid.uuid4()), "method": "transcribe",
        "workspaceRoot": str(workspace), "outputRoot": str(output),
        "params": {
            "audioPath": str(audio), "modelDirectory": str(model),
            "inputAudioSha256": digest, "inputAudioByteSize": audio.stat().st_size,
            "languagePolicy": "ja",
        },
    }, operation


def invoke(request: dict | bytes) -> tuple[int, dict, bytes]:
    payload = request if isinstance(request, bytes) else json.dumps(request).encode("utf-8")
    output = io.BytesIO()
    code = runner.run_task(io.BytesIO(payload), output)
    raw = output.getvalue()
    return code, json.loads(raw), raw


def result_path(request: dict) -> Path:
    return Path(request["outputRoot"]) / (request["requestId"] + ".json")


def test_transcribe_calls_real_entrypoint_and_returns_only_closed_metadata(task: tuple[dict, Mock]) -> None:
    request, operation = task
    code, metadata, stdout = invoke(request)
    operation.assert_called_once_with(Path(request["params"]["audioPath"]),
                                      Path(request["params"]["modelDirectory"]), language_policy="ja")
    data = result_path(request).read_bytes()
    assert code == 0
    assert metadata == {
        "version": 1, "status": "ok", "requestId": request["requestId"], "method": "transcribe",
        "sha256": hashlib.sha256(data).hexdigest(), "byteSize": len(data),
    }
    assert stdout.count(b"\n") == 1
    assert b"private" not in stdout and str(Path(request["outputRoot"])).encode() not in stdout
    result = json.loads(data)["result"]
    assert result["segments"][0]["tokens"][0]["text"] == " private 音声"
    assert result["segments"][0]["start_seconds"] == 0.1
    assert not tuple(Path(request["outputRoot"]).glob("*.partial"))


@pytest.mark.parametrize("payload", [
    b"", b"{}{}", b"[]", b"{\"version\":1,\"version\":1}", b"{\"x\":NaN}",
    b"{\"x\":1e9999}", b"{\"x\":\"\\ud800\"}", b"\xff", b"\xef\xbb\xbf{}",
    b" " * (runner.MAX_REQUEST_BYTES + 1), b"[" * 1100 + b"]" * 1100,
], ids=["empty", "two-docs", "array", "duplicate", "nan", "overflow", "surrogate",
        "utf8", "bom", "oversize", "depth"])
def test_malformed_payload_is_private_and_bounded(payload: bytes) -> None:
    code, metadata, raw = invoke(payload)
    assert code == 1
    assert metadata == {"version": 1, "status": "error", "code": "task.invalid_request"}
    assert len(raw) < 128


@pytest.mark.parametrize("mutation", ["method", "version-bool", "extra-root", "extra-param", "missing-param",
                                      "size-bool", "hash-upper", "hash-short", "policy", "uuid"])
def test_closed_request_rejects_unknown_or_mistyped_fields(task: tuple[dict, Mock], mutation: str) -> None:
    request, operation = task
    if mutation == "method": request["method"] = "shell"
    elif mutation == "version-bool": request["version"] = True
    elif mutation == "extra-root": request["shell"] = "private-command"
    elif mutation == "extra-param": request["params"]["filter"] = "private-code"
    elif mutation == "missing-param": del request["params"]["modelDirectory"]
    elif mutation == "size-bool": request["params"]["inputAudioByteSize"] = True
    elif mutation == "hash-upper": request["params"]["inputAudioSha256"] = "A" * 64
    elif mutation == "hash-short": request["params"]["inputAudioSha256"] = "a"
    elif mutation == "policy": request["params"]["languagePolicy"] = "en"
    else: request["requestId"] = "../escape"
    assert invoke(request)[1]["code"] == "task.invalid_request"
    operation.assert_not_called()


@pytest.mark.parametrize("target", ["audioPath", "modelDirectory", "outputRoot", "workspaceRoot"])
def test_paths_must_be_canonical_existing_and_correct_kind(task: tuple[dict, Mock], target: str) -> None:
    request, operation = task
    owner = request["params"] if target in request["params"] else request
    owner[target] = "relative/private"
    assert invoke(request)[1]["code"] == "task.invalid_path"
    operation.assert_not_called()


def test_output_requires_managed_staging_and_audio_requires_managed_child(task: tuple[dict, Mock]) -> None:
    request, operation = task
    request["outputRoot"] = request["workspaceRoot"]
    assert invoke(request)[1]["code"] == "task.invalid_path"
    operation.assert_not_called()


def test_ancestor_reparse_is_rejected_without_following_it(
    task: tuple[dict, Mock], monkeypatch: pytest.MonkeyPatch,
) -> None:
    request, operation = task
    root = Path(request["workspaceRoot"])
    original = Path.lstat
    observed = []

    def reparse(path: Path, *args, **kwargs):
        observed.append(path)
        if path == root:
            return SimpleNamespace(st_mode=stat.S_IFDIR, st_file_attributes=0x400)
        if root in path.parents:
            raise AssertionError("A denied ancestor must prevent descendant IO.")
        return original(path, *args, **kwargs)

    monkeypatch.setattr(Path, "lstat", reparse)
    with pytest.raises(runner.TaskError) as error:
        runner.require_path(str(root / "unprobed" / "audio.wav"), directory=False)
    assert error.value.code == "task.invalid_path"
    assert observed == [*reversed(root.parents), root]
    observed.clear()
    assert invoke(request)[1]["code"] == "task.invalid_path"
    operation.assert_not_called()


def test_symlink_input_is_rejected(task: tuple[dict, Mock]) -> None:
    request, operation = task
    target = Path(request["params"]["audioPath"])
    link = target.with_name("link.wav")
    try:
        link.symlink_to(target)
    except OSError:
        pytest.skip("Creating symlinks requires platform permission.")
    request["params"]["audioPath"] = str(link)
    assert invoke(request)[1]["code"] == "task.invalid_path"
    operation.assert_not_called()


@pytest.mark.parametrize("mode,attributes", [
    (stat.S_IFREG, 0x400), (stat.S_IFDIR, 0x400), (stat.S_IFLNK, 0),
], ids=["file-reparse", "directory-reparse", "symlink"])
def test_model_entry_is_rejected_before_any_target_following_probe(
    task: tuple[dict, Mock], monkeypatch: pytest.MonkeyPatch, mode: int, attributes: int,
) -> None:
    request, operation = task
    model_file = Path(request["params"]["modelDirectory"]) / "model.bin"
    model_file.write_bytes(b"not a real model")
    original = Path.lstat
    original_stat = Path.stat
    original_is_dir = Path.is_dir
    followed = []

    def reparse(path: Path, *args, **kwargs):
        if path == model_file:
            return SimpleNamespace(st_mode=mode, st_file_attributes=attributes)
        return original(path, *args, **kwargs)

    def observe_stat(path: Path, *, follow_symlinks: bool = True):
        if path == model_file and follow_symlinks:
            followed.append(path)
            raise OSError("private symlink target must not be accessed")
        return original_stat(path, follow_symlinks=follow_symlinks)

    def observe_is_dir(path: Path, *args, **kwargs):
        # Some Python versions bypass Path.stat inside is_dir; catch both surfaces.
        if path == model_file:
            followed.append(path)
            raise AssertionError("Model entry kind must come from lstat, not is_dir.")
        return original_is_dir(path, *args, **kwargs)

    monkeypatch.setattr(Path, "lstat", reparse)
    monkeypatch.setattr(Path, "stat", observe_stat)
    monkeypatch.setattr(Path, "is_dir", observe_is_dir)
    assert invoke(request)[1]["code"] == "task.invalid_path"
    assert followed == []
    operation.assert_not_called()


def test_request_limit_allows_exact_boundary_with_one_document(task: tuple[dict, Mock]) -> None:
    request, _ = task
    payload = json.dumps(request).encode("utf-8")
    payload += b" " * (runner.MAX_REQUEST_BYTES - len(payload))
    assert len(payload) == runner.MAX_REQUEST_BYTES
    assert invoke(payload)[0] == 0


@pytest.mark.parametrize("mutation", ["digest", "size", "changed-after"])
def test_audio_binding_checks_before_and_after_inference(task: tuple[dict, Mock], mutation: str) -> None:
    request, operation = task
    if mutation == "digest": request["params"]["inputAudioSha256"] = "0" * 64
    elif mutation == "size": request["params"]["inputAudioByteSize"] += 1
    else:
        def mutate(*args, **kwargs):
            Path(request["params"]["audioPath"]).write_bytes(b"changed")
            return operation.return_value
        operation.side_effect = mutate
    assert invoke(request)[1]["code"] == "task.input_mismatch"
    assert not result_path(request).exists()
    assert operation.call_count == (1 if mutation == "changed-after" else 0)


def test_empty_speech_is_allowed_and_inference_logs_never_reach_stdout(task: tuple[dict, Mock]) -> None:
    request, operation = task
    def silent(*args, **kwargs):
        print("private stdout")
        print("private stderr", file=sys.stderr)
        return replace(operation.return_value, segments=())
    operation.side_effect = silent
    code, _, raw = invoke(request)
    assert code == 0 and b"private" not in raw
    assert json.loads(result_path(request).read_bytes())["result"]["segments"] == []


def test_nonfinite_result_is_not_published(task: tuple[dict, Mock]) -> None:
    request, operation = task
    operation.return_value = replace(operation.return_value, engine_duration_seconds=math.nan)
    assert invoke(request)[0] == 1
    assert not result_path(request).exists()


def test_result_limit_is_checked_before_file_creation(task: tuple[dict, Mock], monkeypatch: pytest.MonkeyPatch) -> None:
    request, _ = task
    monkeypatch.setattr(runner, "MAX_RESULT_BYTES", 32)
    assert invoke(request)[1]["code"] == "task.result_too_large"
    assert not tuple(Path(request["outputRoot"]).iterdir())


@pytest.mark.parametrize("total_tokens", [99999, 100000, 100001], ids=["below-cap", "at-cap", "over-cap"])
def test_total_token_limit_across_many_segments_before_success(
    task: tuple[dict, Mock], monkeypatch: pytest.MonkeyPatch, total_tokens: int,
) -> None:
    request, operation = task
    token = asr.AsrToken("x", None, None, 0.9, True)
    operation.return_value = replace(operation.return_value, segments=tuple(
        asr.AsrSegment(index, 0, "x", 0.0, 1.0, -0.2, 0.01,
                       (token,) * min(1000, total_tokens - offset))
        for index, offset in enumerate(range(0, total_tokens, 1000))
    ))
    serialize = Mock(wraps=runner.asdict)
    monkeypatch.setattr(runner, "asdict", serialize)
    assert all(len(segment.tokens) <= 1000 for segment in operation.return_value.segments)
    code, metadata, raw = invoke(request)
    operation.assert_called_once()
    if total_tokens > runner.MAX_ITEMS:
        assert code == 1
        assert metadata == {"version": 1, "status": "error", "code": "task.result_too_large"}
        assert b"private" not in raw
        serialize.assert_not_called()
        assert not tuple(Path(request["outputRoot"]).iterdir())
    else:
        assert code == 0 and metadata["status"] == "ok"
        serialize.assert_called_once()
        data = result_path(request).read_bytes()
        assert len(data) <= runner.MAX_RESULT_BYTES
        assert metadata["sha256"] == hashlib.sha256(data).hexdigest()
        assert sum(len(segment["tokens"]) for segment in json.loads(data)["result"]["segments"]) == total_tokens


def test_existing_output_is_never_overwritten(task: tuple[dict, Mock]) -> None:
    request, operation = task
    result_path(request).write_bytes(b"existing")
    assert invoke(request)[1]["code"] == "task.output_failed"
    assert result_path(request).read_bytes() == b"existing"
    operation.assert_not_called()


def test_atomic_publication_collision_keeps_other_file_and_removes_owned_temp(
    task: tuple[dict, Mock], monkeypatch: pytest.MonkeyPatch,
) -> None:
    request, _ = task
    def collide(source, destination, **kwargs):
        Path(destination).write_bytes(b"other-owner")
        raise FileExistsError("private path")
    monkeypatch.setattr(os, "rename" if os.name == "nt" else "link", collide)
    assert invoke(request)[1]["code"] == "task.output_failed"
    assert result_path(request).read_bytes() == b"other-owner"
    assert len(tuple(Path(request["outputRoot"]).iterdir())) == 1


def test_publication_temporary_never_requires_a_longer_path_than_destination(
    task: tuple[dict, Mock], monkeypatch: pytest.MonkeyPatch,
) -> None:
    request, _ = task
    output = Path(request["outputRoot"])
    maximum = len(str(result_path(request)))
    original_open = Path.open
    created = []

    def limited_open(path: Path, mode="r", *args, **kwargs):
        if path.parent == output and mode == "xb":
            created.append(path)
            if len(str(path)) > maximum:
                raise FileNotFoundError("simulated legacy Windows path boundary")
        return original_open(path, mode, *args, **kwargs)

    monkeypatch.setattr(Path, "open", limited_open)
    code, metadata, _ = invoke(request)
    assert code == 0 and metadata["status"] == "ok"
    assert len(created) == 1 and len(str(created[0])) <= maximum
    assert not created[0].exists()
    assert list(output.iterdir()) == [result_path(request)]


def test_temporary_collision_is_not_removed_or_retried(
    task: tuple[dict, Mock], monkeypatch: pytest.MonkeyPatch,
) -> None:
    request, _ = task
    random_id = uuid.UUID("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee")
    temporary = Path(request["outputRoot"]) / f".{random_id.hex}.tmp"
    temporary.write_bytes(b"another owner")
    new_id = Mock(return_value=random_id)
    monkeypatch.setattr(runner.uuid, "uuid4", new_id)
    assert invoke(request)[1]["code"] == "task.output_failed"
    new_id.assert_called_once()
    assert temporary.read_bytes() == b"another owner"
    assert not result_path(request).exists()


def test_publication_in_deep_native_staging_directory(tmp_path: Path) -> None:
    # Keep the final path within 259 characters even on Windows without long paths.
    # The old temporary naming would exceed it; do not require long-path OS settings.
    available = 210 - len(str(tmp_path)) - 1
    if available < 8:
        pytest.skip("Test parent is already too long for the legacy path fixture.")
    output = tmp_path / ("d" * available)
    output.mkdir()
    request_id = str(uuid.uuid4())
    assert len(str(output / f"{request_id}.json")) <= 259
    assert len(str(output / f".{request_id}.{uuid.uuid4()}.partial")) > 259
    data = b'{"result":"actual test bytes"}'
    runner._publish(output, request_id, data)
    assert (output / f"{request_id}.json").read_bytes() == data
    assert len(list(output.iterdir())) == 1


@pytest.mark.parametrize("error", [RuntimeError("private path/text"), KeyboardInterrupt(), asr.AsrTranscriptionError(asr.INVALID_MODEL)])
def test_errors_and_interrupt_are_private(task: tuple[dict, Mock], error: BaseException) -> None:
    request, operation = task
    operation.side_effect = error
    code, metadata, raw = invoke(request)
    assert code == 1 and metadata["code"] in runner.ERROR_CODES
    assert b"private" not in raw and not result_path(request).exists()


def test_repetition_uses_real_dto_and_real_candidates(task: tuple[dict, Mock]) -> None:
    request, operation = task
    request["method"] = "repetition"
    token_ids = [str(uuid.uuid4()), str(uuid.uuid4())]
    request["params"] = {"modelRunId": str(uuid.uuid4()), "transcript": {
        "revision_id": str(uuid.uuid4()), "revision_digest": "a" * 64, "utterances": [{
            "utterance_id": str(uuid.uuid4()), "source_span_id": str(uuid.uuid4()),
            "speaker_id": str(uuid.uuid4()), "sentences": [{"sentence_id": str(uuid.uuid4()),
                "tokens": [{"token_id": token, "text": "繰り返し"} for token in token_ids]}],
        }],
    }}
    assert invoke(request)[0] == 0
    suggestions = json.loads(result_path(request).read_bytes())["result"]["suggestions"]
    assert len(suggestions) == 1
    assert suggestions[0]["evidence_token_ids"] == token_ids[1:]
    assert suggestions[0]["automatic_deletion_allowed"] is False
    operation.assert_not_called()


def test_cli_task_errors_are_private_and_modes_remain_exclusive() -> None:
    for arguments, payload in [(["--task"], b"{}"), (["--task", "--health"], b""),
                               (["--task", "--protocol"], b""), (["--task", "private-arg"], b"")]:
        completed = subprocess.run([sys.executable, "-m", "hubradio_worker", *arguments],
                                   input=payload, capture_output=True, timeout=10, check=False)
        assert completed.returncode == 1
        assert completed.stderr == b""
        assert json.loads(completed.stdout) == {"version": 1, "status": "error", "code": "task.invalid_request"}


@pytest.mark.parametrize("arguments", [
    ["--task=PRIVATE_VALUE"],
    ["--task=PRIVATE_VALUE", "--health"],
    ["--protocol", "--task=PRIVATE_VALUE"],
    ["--help", "--task=PRIVATE_VALUE"],
    ["--task", "--task=PRIVATE_VALUE"],
], ids=["equals", "equals-health", "equals-protocol", "equals-help", "equals-duplicate"])
def test_cli_equals_task_argument_never_leaks_secret(arguments: list[str]) -> None:
    completed = subprocess.run([sys.executable, "-m", "hubradio_worker", *arguments],
                               input=b"", capture_output=True, timeout=10, check=False)
    assert completed.returncode == 1
    assert b"PRIVATE_VALUE" not in completed.stdout
    assert b"PRIVATE_VALUE" not in completed.stderr
    assert completed.stderr == b""
    assert completed.stdout.count(b"\n") == 1
    assert json.loads(completed.stdout) == {"version": 1, "status": "error", "code": "task.invalid_request"}