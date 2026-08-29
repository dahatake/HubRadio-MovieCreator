from __future__ import annotations

import hashlib
import math
import wave
from collections.abc import Callable
from dataclasses import FrozenInstanceError
from pathlib import Path
from types import SimpleNamespace

import pytest  # pyright: ignore[reportMissingImports] - declared test extra; worker venv verified.

from hubradio_worker.tasks import transcribe as asr


class FakeModel:
    def __init__(
        self,
        segments: list[object],
        information: object,
        *,
        failure: Exception | None = None,
        mutate: Callable[[], None] | None = None,
    ) -> None:
        self._segments = segments
        self._information = information
        self._failure = failure
        self._mutate = mutate
        self.options: dict[str, object] | None = None

    def transcribe(
        self,
        audio: str,
        **options: object,
    ) -> tuple[list[object], object]:
        self.options = {"audio": audio, **options}
        if self._mutate is not None:
            self._mutate()
        if self._failure is not None:
            raise self._failure
        return self._segments, self._information


def _sha256(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def _pinned_model(monkeypatch: pytest.MonkeyPatch, root: Path) -> Path:
    root.mkdir()
    (root / ".cache").mkdir()
    contents = {
        "config.json": b"config",
        "model.bin": b"model",
        "tokenizer.json": b"tokenizer",
        "vocabulary.txt": b"vocabulary",
    }
    descriptors = tuple(
        asr.PinnedModelFile(name, len(content), _sha256(content))
        for name, content in sorted(contents.items())
    )
    records = "".join(
        f"{item.file_name}\0{item.byte_size}\0{item.sha256}\n"
        for item in descriptors
    )
    for name, content in contents.items():
        (root / name).write_bytes(content)
    monkeypatch.setattr(asr, "PINNED_MODEL_FILES", descriptors)
    monkeypatch.setattr(
        asr,
        "MODEL_MANIFEST_SHA256",
        hashlib.sha256(records.encode("utf-8")).hexdigest(),
    )
    return root


def _audio(path: Path, *, channels: int = 1, width: int = 2, rate: int = 16000) -> Path:
    with wave.open(str(path), "wb") as output:
        output.setnchannels(channels)
        output.setsampwidth(width)
        output.setframerate(rate)
        output.writeframes(b"\0" * channels * width * 1600)
    return path


def _word(
    text: str = " テスト",
    start: float | None = 0.1,
    end: float | None = 0.5,
    probability: float = 0.9,
) -> object:
    return SimpleNamespace(
        word=text,
        start=start,
        end=end,
        probability=probability,
    )


def _segment(
    *,
    segment_id: int = 0,
    start: float = 0.0,
    end: float = 1.0,
    words: list[object] | None = None,
) -> object:
    return SimpleNamespace(
        id=segment_id,
        seek=0,
        start=start,
        end=end,
        text=" テストです。",
        avg_logprob=-0.2,
        no_speech_prob=0.01,
        words=[_word()] if words is None else words,
    )


def _information() -> object:
    return SimpleNamespace(
        language="ja",
        language_probability=1.0,
        duration=0.1,
    )


def _factory_returning(model: FakeModel) -> Callable[..., FakeModel]:
    def factory(*arguments: object, **options: object) -> FakeModel:
        del arguments, options
        return model

    return factory


def _unexpected_factory(*arguments: object, **options: object) -> FakeModel:
    del arguments, options
    raise AssertionError("The model factory must not be called for invalid input.")


def _run(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
    model: FakeModel,
) -> tuple[asr.AsrTranscriptionResult, dict[str, object]]:
    model_path = _pinned_model(monkeypatch, tmp_path / "model")
    audio_path = _audio(tmp_path / "audio.wav")
    factory_call: dict[str, object] = {}

    def factory(path: str, **options: object) -> FakeModel:
        factory_call.update({"path": path, **options})
        return model

    result = asr.transcribe(audio_path, model_path, _model_factory=factory)
    return result, factory_call


def test_fixed_cpu_runner_returns_immutable_segments_tokens_and_confidence(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
) -> None:
    model = FakeModel(
        [
            _segment(
                words=[
                    _word(" テスト", 0.1, 0.5, 0.95),
                    _word("です", 0.5, 1.1, 0.75),
                    _word("。", None, None, 0.5),
                ]
            )
        ],
        _information(),
    )

    result, factory = _run(monkeypatch, tmp_path, model)

    assert result.model_repository == asr.MODEL_REPOSITORY
    assert result.model_revision == asr.MODEL_REVISION
    assert result.model_manifest_sha256 == asr.MODEL_MANIFEST_SHA256
    assert result.configuration == asr.AsrConfiguration()
    assert len(result.input_audio_sha256) == 64
    assert result.input_audio_byte_size > 44
    assert result.input_audio_duration_seconds == 0.1
    assert result.language == "ja"
    assert result.language_confidence == 1.0
    assert result.engine_duration_seconds == 0.1
    assert len(result.segments) == 1
    tokens = result.segments[0].tokens
    assert tokens[0] == asr.AsrToken(" テスト", 0.1, 0.5, 0.95, False)
    assert tokens[1] == asr.AsrToken("です", 0.5, 1.1, 0.75, True)
    assert tokens[2] == asr.AsrToken("。", None, None, 0.5, True)
    assert factory == {
        "path": str(tmp_path / "model"),
        "device": "cpu",
        "compute_type": "int8",
        "cpu_threads": 0,
        "num_workers": 1,
        "local_files_only": True,
    }
    assert model.options == {
        "audio": str(tmp_path / "audio.wav"),
        "language": "ja",
        "task": "transcribe",
        "beam_size": 5,
        "word_timestamps": True,
        "vad_filter": False,
        "condition_on_previous_text": True,
    }
    with pytest.raises(FrozenInstanceError):
        result.segments[0].tokens[0].text = "変更"  # type: ignore[misc]


def test_silence_may_return_no_segments_without_inventing_text(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
) -> None:
    result, _ = _run(monkeypatch, tmp_path, FakeModel([], _information()))

    assert result.segments == ()


def test_untimed_and_zero_duration_tokens_preserve_raw_state_for_later_review(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
) -> None:
    model = FakeModel(
        [_segment(words=[_word("時刻なし", None, None), _word("ゼロ", 0.5, 0.5)])],
        _information(),
    )

    result, _ = _run(monkeypatch, tmp_path, model)

    assert result.segments[0].tokens == (
        asr.AsrToken("時刻なし", None, None, 0.9, True),
        asr.AsrToken("ゼロ", 0.5, 0.5, 0.9, True),
    )


@pytest.mark.parametrize(
    "mutation",
    ["missing", "extra-file", "extra-directory", "changed"],
)
def test_model_manifest_must_match_every_pinned_file(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
    mutation: str,
) -> None:
    model_path = _pinned_model(monkeypatch, tmp_path / "model")
    audio_path = _audio(tmp_path / "audio.wav")
    if mutation == "missing":
        (model_path / "config.json").unlink()
    elif mutation == "extra-file":
        (model_path / "extra.bin").write_bytes(b"extra")
    elif mutation == "extra-directory":
        (model_path / "unexpected-directory").mkdir()
    else:
        (model_path / "model.bin").write_bytes(b"changed")

    with pytest.raises(asr.AsrTranscriptionError) as captured:
        asr.transcribe(audio_path, model_path, _model_factory=_unexpected_factory)

    assert captured.value.code == asr.INVALID_MODEL
    assert str(captured.value) == "The ASR transcription operation failed."
    assert str(model_path) not in str(captured.value)


@pytest.mark.parametrize(
    ("channels", "width", "rate"),
    [(2, 2, 16000), (1, 1, 16000), (1, 2, 8000)],
)
def test_only_nonempty_16khz_mono_pcm_s16le_is_accepted(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
    channels: int,
    width: int,
    rate: int,
) -> None:
    model_path = _pinned_model(monkeypatch, tmp_path / "model")
    audio_path = _audio(
        tmp_path / "private-audio.wav",
        channels=channels,
        width=width,
        rate=rate,
    )

    with pytest.raises(asr.AsrTranscriptionError) as captured:
        asr.transcribe(audio_path, model_path, _model_factory=_unexpected_factory)

    assert captured.value.code == asr.INVALID_AUDIO
    assert str(audio_path) not in str(captured.value)


def test_relative_missing_empty_and_nonwave_inputs_fail_without_path_disclosure(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
) -> None:
    model_path = _pinned_model(monkeypatch, tmp_path / "model")
    invalid_paths = [
        Path("relative.wav"),
        tmp_path / "missing.wav",
        tmp_path / "empty.wav",
        tmp_path / "not-wave.wav",
    ]
    invalid_paths[2].write_bytes(b"")
    invalid_paths[3].write_bytes(b"private input text")

    for invalid in invalid_paths:
        with pytest.raises(asr.AsrTranscriptionError) as captured:
            asr.transcribe(invalid, model_path, _model_factory=_unexpected_factory)
        assert captured.value.code == asr.INVALID_AUDIO
        assert str(invalid) not in str(captured.value)
        assert "private input text" not in str(captured.value)


@pytest.mark.parametrize(
    "segments",
    [
        [_segment(start=math.nan)],
        [_segment(end=-1.0)],
        [_segment(start=0.0, end=0.0)],
        [_segment(words=[])],
        [_segment(words=[_word(probability=1.01)])],
        [_segment(words=[_word(start=0.5, end=0.4)])],
        [_segment(words=[_word(start=None, end=0.4)])],
        [_segment(segment_id=0), _segment(segment_id=0, start=1.0, end=2.0)],
        [_segment(start=1.0, end=2.0), _segment(segment_id=1, start=0.5, end=1.0)],
    ],
)
def test_invalid_engine_output_is_rejected_with_stable_private_failure(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
    segments: list[object],
) -> None:
    model = FakeModel(segments, _information())
    model_path = _pinned_model(monkeypatch, tmp_path / "model")
    audio_path = _audio(tmp_path / "private-audio.wav")

    with pytest.raises(asr.AsrTranscriptionError) as captured:
        asr.transcribe(audio_path, model_path, _model_factory=_factory_returning(model))

    assert captured.value.code == asr.PROCESSING_FAILED
    assert str(captured.value) == "The ASR transcription operation failed."
    assert "private-audio" not in str(captured.value)


def test_runtime_failure_and_missing_dependency_are_sanitized(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
) -> None:
    model_path = _pinned_model(monkeypatch, tmp_path / "model")
    audio_path = _audio(tmp_path / "audio.wav")
    failed = FakeModel([], _information(), failure=RuntimeError("private model detail"))

    with pytest.raises(asr.AsrTranscriptionError) as captured:
        asr.transcribe(audio_path, model_path, _model_factory=_factory_returning(failed))
    assert captured.value.code == asr.PROCESSING_FAILED
    assert "private model detail" not in str(captured.value)

    def missing(_: str) -> None:
        raise ModuleNotFoundError("private import detail")

    monkeypatch.setattr(asr.importlib, "import_module", missing)
    with pytest.raises(asr.AsrTranscriptionError) as captured:
        asr.transcribe(audio_path, model_path)
    assert captured.value.code == asr.RUNTIME_UNAVAILABLE
    assert "private import detail" not in str(captured.value)

    def failed_factory(*arguments: object, **options: object) -> FakeModel:
        del arguments, options
        raise RuntimeError("private model initialization detail")

    with pytest.raises(asr.AsrTranscriptionError) as captured:
        asr.transcribe(audio_path, model_path, _model_factory=failed_factory)
    assert captured.value.code == asr.PROCESSING_FAILED
    assert "private model initialization detail" not in str(captured.value)


@pytest.mark.parametrize("target", ["audio", "model"])
def test_input_or_model_mutation_during_inference_discards_result(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
    target: str,
) -> None:
    model_path = _pinned_model(monkeypatch, tmp_path / "model")
    audio_path = _audio(tmp_path / "audio.wav")

    def mutate() -> None:
        if target == "audio":
            audio_path.write_bytes(audio_path.read_bytes() + b"changed")
        else:
            (model_path / "model.bin").write_bytes(b"changed")

    model = FakeModel([_segment()], _information(), mutate=mutate)
    with pytest.raises(asr.AsrTranscriptionError) as captured:
        asr.transcribe(audio_path, model_path, _model_factory=_factory_returning(model))

    assert captured.value.code == (
        asr.INPUT_CHANGED if target == "audio" else asr.MODEL_CHANGED
    )


def test_unknown_error_code_is_a_programming_error() -> None:
    with pytest.raises(ValueError, match="Unknown ASR transcription error code"):
        asr.AsrTranscriptionError("asr.future")