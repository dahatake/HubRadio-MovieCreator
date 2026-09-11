from __future__ import annotations

from pathlib import Path
from types import SimpleNamespace

import pytest

from hubradio_worker.tasks import transcribe as asr
from test_transcribe import FakeModel, _audio, _factory_returning, _information, _pinned_model


def test_explicit_auto_passes_none_and_keeps_actual_detection(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    model_path = _pinned_model(monkeypatch, tmp_path / "model")
    audio_path = _audio(tmp_path / "audio.wav")
    model = FakeModel([], SimpleNamespace(language="en", language_probability=0.73, duration=0.1))
    result = asr.transcribe(audio_path, model_path, language_policy=asr.AUTO_LANGUAGE_POLICY,
                            _model_factory=_factory_returning(model))
    assert model.options is not None and model.options["language"] is None
    assert result.configuration == asr.AUTO_CONFIGURATION
    assert result.language == "en" and result.language_confidence == 0.73
    assert result.language_detection == asr.LanguageDetectionEvidence(asr.AUTO_LANGUAGE_POLICY, "en", 0.73, None)
    assert result.segments == ()


@pytest.mark.parametrize("reason", ["absent", "error"])
def test_only_detector_failure_retries_real_japanese_inference(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, reason: str,
) -> None:
    model_path = _pinned_model(monkeypatch, tmp_path / "model")
    audio_path = _audio(tmp_path / "audio.wav")
    calls = []
    class DetectionFailureModel:
        def transcribe(self, audio, **options):
            calls.append(options["language"])
            if options["language"] is None:
                if reason == "error":
                    raise asr._LanguageDetectionUnavailable("error")
                return [], SimpleNamespace(language=None)
            return [], _information()
    result = asr.transcribe(audio_path, model_path, language_policy=asr.AUTO_LANGUAGE_POLICY,
                            _model_factory=lambda *args, **kwargs: DetectionFailureModel())
    assert calls == [None, "ja"]
    assert result.language == "ja"
    assert result.configuration == asr.AUTO_CONFIGURATION
    assert result.language_detection == asr.LanguageDetectionEvidence(asr.AUTO_LANGUAGE_POLICY, None, None, reason)


def test_actual_model_detector_boundary_tags_error_without_swallowing_generation_errors(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch,
) -> None:
    model_path = _pinned_model(monkeypatch, tmp_path / "model")
    audio_path = _audio(tmp_path / "audio.wav")
    calls = []
    class Model:
        def __init__(self, *args, **kwargs):
            assert kwargs["local_files_only"] is True
        def detect_language(self, **kwargs):
            raise RuntimeError("private detector detail")
        def transcribe(self, audio, **kwargs):
            calls.append(kwargs["language"])
            if kwargs["language"] is None:
                self.detect_language(features=object())
            return [], _information()
    monkeypatch.setattr(asr.importlib, "import_module", lambda name: SimpleNamespace(WhisperModel=Model))
    result = asr.transcribe(audio_path, model_path, language_policy=asr.AUTO_LANGUAGE_POLICY)
    assert calls == [None, "ja"]
    assert result.language_detection.fallback_reason == "error"


def test_generic_failure_does_not_retry_or_fabricate_language(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    model_path = _pinned_model(monkeypatch, tmp_path / "model")
    audio_path = _audio(tmp_path / "audio.wav")
    calls = []
    class BrokenModel:
        def transcribe(self, audio, **options):
            calls.append(options["language"])
            raise RuntimeError("private inference error")
    with pytest.raises(asr.AsrTranscriptionError) as captured:
        asr.transcribe(audio_path, model_path, language_policy=asr.AUTO_LANGUAGE_POLICY,
                       _model_factory=lambda *args, **kwargs: BrokenModel())
    assert captured.value.code == asr.PROCESSING_FAILED
    assert calls == [None]


@pytest.mark.parametrize("language, probability", [("unknown", 0.5), ("en", float("nan")), ("en", 1.1)])
def test_invalid_detection_is_not_treated_as_absent(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, language: str, probability: float,
) -> None:
    model_path = _pinned_model(monkeypatch, tmp_path / "model")
    audio_path = _audio(tmp_path / "audio.wav")
    model = FakeModel([], SimpleNamespace(language=language, language_probability=probability, duration=0.1))
    with pytest.raises(asr.AsrTranscriptionError):
        asr.transcribe(audio_path, model_path, language_policy=asr.AUTO_LANGUAGE_POLICY,
                       _model_factory=_factory_returning(model))


def test_lazy_generation_failure_is_not_a_detection_fallback(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    model_path = _pinned_model(monkeypatch, tmp_path / "model")
    audio_path = _audio(tmp_path / "audio.wav")
    calls = []
    def broken_segments():
        raise RuntimeError("private generation error")
        yield  # pragma: no cover - this is deliberately a lazy generator
    class BrokenGeneratorModel:
        def transcribe(self, audio, **options):
            calls.append(options["language"])
            return broken_segments(), _information()
    with pytest.raises(asr.AsrTranscriptionError):
        asr.transcribe(audio_path, model_path, language_policy=asr.AUTO_LANGUAGE_POLICY,
                       _model_factory=lambda *args, **kwargs: BrokenGeneratorModel())
    assert calls == [None]