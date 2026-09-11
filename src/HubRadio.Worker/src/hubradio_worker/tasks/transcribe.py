"""Pinned local Japanese ASR inference without transport or source-time mapping.

The CPU/int8 path is intentional until a redistributable CUDA runtime is approved.
All model segments are retained regardless of no-speech probability; later analysis owns
silence suggestions. This module returns raw ASR candidates rather than the persisted
TranscriptRevision shape. A-003 owns rational source-time conversion and decides whether a
review-only token time can become a persisted range.
"""

from __future__ import annotations

import hashlib
import importlib
import math
import os
import wave
from collections.abc import Callable, Iterable
from dataclasses import dataclass
from pathlib import Path
from typing import Protocol, cast

MODEL_REPOSITORY = "Systran/faster-whisper-small"
MODEL_REVISION = "536b0662742c02347bc0e980a01041f333bce120"
MODEL_MANIFEST_SHA256 = "4e924b13871cadaa18f24bf888a6388e45d15da56e06b0b90141dc27d96e951f"

INVALID_AUDIO = "asr.invalid_audio"
INVALID_MODEL = "asr.invalid_model"
INPUT_CHANGED = "asr.input_changed"
MODEL_CHANGED = "asr.model_changed"
RUNTIME_UNAVAILABLE = "asr.runtime_unavailable"
PROCESSING_FAILED = "asr.processing_failed"

_ERROR_CODES = frozenset(
    {
        INVALID_AUDIO,
        INVALID_MODEL,
        INPUT_CHANGED,
        MODEL_CHANGED,
        RUNTIME_UNAVAILABLE,
        PROCESSING_FAILED,
    }
)
_ERROR_MESSAGE = "The ASR transcription operation failed."
_HASH_CHUNK_BYTES = 1024 * 1024
_ALLOWED_METADATA_DIRECTORIES = frozenset({".cache"})


@dataclass(frozen=True, slots=True)
class PinnedModelFile:
    file_name: str
    byte_size: int
    sha256: str


PINNED_MODEL_FILES = (
    PinnedModelFile(
        "config.json",
        2370,
        "b55496ac7940a7ae47d2c01eab40edfd8701feec1229d9cce3b40014383fb828",
    ),
    PinnedModelFile(
        "model.bin",
        483546902,
        "3e305921506d8872816023e4c273e75d2419fb89b24da97b4fe7bce14170d671",
    ),
    PinnedModelFile(
        "tokenizer.json",
        2203239,
        "fb7b63191e9bb045082c79fd742a3106a12c99513ab30df4a0d47fa6cb6fd0ab",
    ),
    PinnedModelFile(
        "vocabulary.txt",
        459861,
        "34ce3fe1c5041027b3f8d42912270993f986dbc4bb34cf27f951e34a1e453913",
    ),
)


@dataclass(frozen=True, slots=True)
class AsrConfiguration:
    device: str = "cpu"
    compute_type: str = "int8"
    language: str = "ja"
    task: str = "transcribe"
    beam_size: int = 5
    word_timestamps: bool = True
    vad_filter: bool = False
    condition_on_previous_text: bool = True


PINNED_CONFIGURATION = AsrConfiguration()
AUTO_LANGUAGE_POLICY = "autoDetectWithJaJpFallback"
AUTO_CONFIGURATION = AsrConfiguration(language=AUTO_LANGUAGE_POLICY)
# Language tokens supported by the pinned faster-whisper 1.2.1 tokenizer.
SUPPORTED_LANGUAGES = frozenset(
    "af am ar as az ba be bg bn bo br bs ca cs cy da de el en es et eu fa fi fo fr "
    "gl gu ha haw he hi hr ht hu hy id is it ja jw ka kk km kn ko la lb ln lo lt lv "
    "mg mi mk ml mn mr ms mt my ne nl nn no oc pa pl ps pt ro ru sa sd si sk sl sn "
    "so sq sr su sv sw ta te tg th tk tl tr tt uk ur uz vi yi yo zh yue".split()
)


@dataclass(frozen=True, slots=True)
class LanguageDetectionEvidence:
    requested_policy: str
    detected_language: str | None
    detected_language_confidence: float | None
    fallback_reason: str | None


class _LanguageDetectionUnavailable(RuntimeError):
    """Raised only at the actual detector boundary, never by general inference."""

    def __init__(self, reason: str) -> None:
        super().__init__("Language detection was unavailable.")
        self.reason = reason


@dataclass(frozen=True, slots=True)
class AsrToken:
    """Raw normalized-audio timing evidence, not a persisted Transcript token."""

    text: str
    start_seconds: float | None
    end_seconds: float | None
    recognition_confidence: float
    timing_requires_review: bool


@dataclass(frozen=True, slots=True)
class AsrSegment:
    segment_id: int
    seek: int
    text: str
    start_seconds: float
    end_seconds: float
    average_log_probability: float
    no_speech_probability: float
    tokens: tuple[AsrToken, ...]


@dataclass(frozen=True, slots=True)
class AsrTranscriptionResult:
    model_repository: str
    model_revision: str
    model_manifest_sha256: str
    configuration: AsrConfiguration
    input_audio_sha256: str
    input_audio_byte_size: int
    input_audio_duration_seconds: float
    language: str
    language_confidence: float
    engine_duration_seconds: float
    segments: tuple[AsrSegment, ...]
    language_detection: LanguageDetectionEvidence = LanguageDetectionEvidence("ja", None, None, None)


class AsrTranscriptionError(RuntimeError):
    """A path- and content-private ASR failure with a stable code."""

    def __init__(self, code: str) -> None:
        if code not in _ERROR_CODES:
            raise ValueError("Unknown ASR transcription error code.")
        super().__init__(_ERROR_MESSAGE)
        self._code = code

    @property
    def code(self) -> str:
        return self._code


class _WhisperModel(Protocol):
    def transcribe(
        self,
        audio: str,
        **options: object,
    ) -> tuple[Iterable[object], object]: ...


_ModelFactory = Callable[..., _WhisperModel]


@dataclass(frozen=True, slots=True)
class _AudioDescriptor:
    sha256: str
    byte_size: int
    duration_seconds: float


def transcribe(
    audio_path: Path,
    model_directory: Path,
    *,
    language_policy: str = "ja",
    _model_factory: _ModelFactory | None = None,
) -> AsrTranscriptionResult:
    """Run the single adopted CPU ASR configuration against normalized PCM WAV.

    Worker transport and source PTS mapping are intentionally outside this inference task.
    The host process owns timeout, cancellation, and resource limits. Model and audio digests
    are checked before and after inference; PK-004 owns the read-only model installation and ACL.
    """

    if type(language_policy) is not str or language_policy not in {"ja", AUTO_LANGUAGE_POLICY}:
        raise AsrTranscriptionError(PROCESSING_FAILED)
    configuration = AUTO_CONFIGURATION if language_policy == AUTO_LANGUAGE_POLICY else PINNED_CONFIGURATION
    audio = _require_canonical_path(audio_path, expect_directory=False, error_code=INVALID_AUDIO)
    model_path = _require_canonical_path(
        model_directory,
        expect_directory=True,
        error_code=INVALID_MODEL,
    )
    audio_before = _describe_audio(audio)
    model_before = _verify_model(model_path, INVALID_MODEL)

    try:
        model = _create_model(model_path, _model_factory)
        options = dict(
            task=configuration.task,
            beam_size=configuration.beam_size,
            word_timestamps=configuration.word_timestamps,
            vad_filter=configuration.vad_filter,
            condition_on_previous_text=configuration.condition_on_previous_text,
        )
        fallback_reason = None
        try:
            raw_segments, information = model.transcribe(
                str(audio), language=None if configuration == AUTO_CONFIGURATION else "ja", **options
            )
            if configuration == AUTO_CONFIGURATION and getattr(information, "language", None) in (None, ""):
                raise _LanguageDetectionUnavailable("absent")
        except _LanguageDetectionUnavailable as detection_error:
            if configuration != AUTO_CONFIGURATION or detection_error.reason not in {"absent", "error"}:
                raise ValueError() from None
            fallback_reason = detection_error.reason
            # A real second inference, not a relabelled result. Other inference failures
            # (including errors from the lazy segment iterator) never trigger fallback.
            raw_segments, information = model.transcribe(str(audio), language="ja", **options)
        language = _required_string(information, "language")
        if language not in SUPPORTED_LANGUAGES or (
            (configuration == PINNED_CONFIGURATION or fallback_reason is not None) and language != "ja"
        ):
            raise ValueError()
        language_confidence = _closed_unit_float(information, "language_probability")
        engine_duration = _nonnegative_float(information, "duration")
        segments = _convert_segments(raw_segments)
        detected = configuration == AUTO_CONFIGURATION and fallback_reason is None
        language_detection = LanguageDetectionEvidence(
            language_policy,
            language if detected else None,
            language_confidence if detected else None,
            fallback_reason,
        )
    except AsrTranscriptionError:
        raise
    except (ImportError, ModuleNotFoundError):
        raise AsrTranscriptionError(RUNTIME_UNAVAILABLE) from None
    except (AttributeError, OSError, RuntimeError, TypeError, ValueError):
        raise AsrTranscriptionError(PROCESSING_FAILED) from None

    try:
        if _sha256(audio) != audio_before.sha256 or audio.stat().st_size != audio_before.byte_size:
            raise AsrTranscriptionError(INPUT_CHANGED)
        # A pre/post digest detects retained mutation, not an attacker that restores the pinned
        # bytes before this check. PK-004 owns the read-only model installation and ACL boundary.
        model_after = _verify_model(model_path, MODEL_CHANGED)
        if model_after != model_before:
            raise AsrTranscriptionError(MODEL_CHANGED)
    except AsrTranscriptionError:
        raise
    except OSError:
        raise AsrTranscriptionError(INPUT_CHANGED) from None

    return AsrTranscriptionResult(
        model_repository=MODEL_REPOSITORY,
        model_revision=MODEL_REVISION,
        model_manifest_sha256=MODEL_MANIFEST_SHA256,
        configuration=configuration,
        input_audio_sha256=audio_before.sha256,
        input_audio_byte_size=audio_before.byte_size,
        input_audio_duration_seconds=audio_before.duration_seconds,
        language=language,
        language_confidence=language_confidence,
        engine_duration_seconds=engine_duration,
        segments=segments,
        language_detection=language_detection,
    )


def _create_model(
    model_directory: Path,
    model_factory: _ModelFactory | None,
) -> _WhisperModel:
    factory = model_factory
    if factory is None:
        module = importlib.import_module("faster_whisper")
        base_model = getattr(module, "WhisperModel")

        def detect_language(self: object, *args: object, **kwargs: object) -> object:
            # Scope fallback to this API, not decoding, initialization, generation,
            # invalid numeric evidence, or arbitrary model exceptions.
            try:
                detection = base_model.detect_language(self, *args, **kwargs)
            except (RuntimeError, ValueError, IndexError):
                raise _LanguageDetectionUnavailable("error") from None
            if detection is None or detection[0] in (None, ""):
                raise _LanguageDetectionUnavailable("absent")
            return detection

        # Keep optional dependency loading lazy; this dynamically derived class has
        # the same transcribe surface as the pinned base model.
        factory = cast(_ModelFactory, type(
            "DetectionAwareModel", (base_model,), {"detect_language": detect_language}
        ))
    return factory(
        str(model_directory),
        device=PINNED_CONFIGURATION.device,
        compute_type=PINNED_CONFIGURATION.compute_type,
        cpu_threads=0,
        num_workers=1,
        local_files_only=True,
    )


def _convert_segments(raw_segments: Iterable[object]) -> tuple[AsrSegment, ...]:
    segments: list[AsrSegment] = []
    segment_ids: set[int] = set()
    previous_segment_start = -math.inf
    previous_token_start = -math.inf

    for raw_segment in raw_segments:
        segment_id = _nonnegative_int(raw_segment, "id")
        if segment_id in segment_ids:
            raise ValueError()
        segment_ids.add(segment_id)
        seek = _nonnegative_int(raw_segment, "seek")
        text = _required_string(raw_segment, "text")
        segment_start = _nonnegative_float(raw_segment, "start")
        segment_end = _nonnegative_float(raw_segment, "end")
        if segment_end <= segment_start or segment_start < previous_segment_start:
            raise ValueError()
        previous_segment_start = segment_start
        average_log_probability = _finite_float(raw_segment, "avg_logprob")
        no_speech_probability = _closed_unit_float(raw_segment, "no_speech_prob")

        raw_words = getattr(raw_segment, "words")
        if raw_words is None:
            raise ValueError()
        tokens: list[AsrToken] = []
        for raw_word in raw_words:
            token, timed_start = _convert_token(raw_word, segment_start, segment_end)
            if timed_start is not None:
                if timed_start < previous_token_start:
                    raise ValueError()
                previous_token_start = timed_start
            tokens.append(token)
        if not tokens:
            raise ValueError()

        segments.append(
            AsrSegment(
                segment_id=segment_id,
                seek=seek,
                text=text,
                start_seconds=segment_start,
                end_seconds=segment_end,
                average_log_probability=average_log_probability,
                no_speech_probability=no_speech_probability,
                tokens=tuple(tokens),
            )
        )

    return tuple(segments)


def _convert_token(
    raw_word: object,
    segment_start: float,
    segment_end: float,
) -> tuple[AsrToken, float | None]:
    text = _required_string(raw_word, "word")
    confidence = _closed_unit_float(raw_word, "probability")
    raw_start = getattr(raw_word, "start")
    raw_end = getattr(raw_word, "end")
    if raw_start is None and raw_end is None:
        return AsrToken(text, None, None, confidence, True), None
    if raw_start is None or raw_end is None:
        raise ValueError()

    start = _nonnegative_number(raw_start)
    end = _nonnegative_number(raw_end)
    if end < start:
        raise ValueError()
    timing_requires_review = end <= start or start < segment_start or end > segment_end
    return AsrToken(text, start, end, confidence, timing_requires_review), start


def _required_string(value: object, attribute: str) -> str:
    result = getattr(value, attribute)
    if type(result) is not str or not result.strip():
        raise ValueError()
    return result


def _nonnegative_int(value: object, attribute: str) -> int:
    result = getattr(value, attribute)
    if type(result) is not int or result < 0:
        raise ValueError()
    return result


def _finite_float(value: object, attribute: str) -> float:
    return _finite_number(getattr(value, attribute))


def _nonnegative_float(value: object, attribute: str) -> float:
    return _nonnegative_number(getattr(value, attribute))


def _closed_unit_float(value: object, attribute: str) -> float:
    result = _finite_number(getattr(value, attribute))
    if result < 0 or result > 1:
        raise ValueError()
    return result


def _finite_number(value: object) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ValueError()
    result = float(value)
    if not math.isfinite(result):
        raise ValueError()
    return result


def _nonnegative_number(value: object) -> float:
    result = _finite_number(value)
    if result < 0:
        raise ValueError()
    return result


def _describe_audio(path: Path) -> _AudioDescriptor:
    try:
        with wave.open(str(path), "rb") as audio:
            frame_count = audio.getnframes()
            sample_rate = audio.getframerate()
            channels = audio.getnchannels()
            sample_width = audio.getsampwidth()
            if (
                audio.getcomptype() != "NONE"
                or channels != 1
                or sample_width != 2
                or sample_rate != 16000
                or frame_count <= 0
            ):
                raise AsrTranscriptionError(INVALID_AUDIO)
            _read_exact_pcm_frames(audio, frame_count, channels * sample_width)
        return _AudioDescriptor(
            sha256=_sha256(path),
            byte_size=path.stat().st_size,
            duration_seconds=frame_count / sample_rate,
        )
    except AsrTranscriptionError:
        raise
    except (OSError, EOFError, wave.Error):
        raise AsrTranscriptionError(INVALID_AUDIO) from None


def _read_exact_pcm_frames(
    audio: wave.Wave_read,
    frame_count: int,
    bytes_per_frame: int,
) -> None:
    remaining = frame_count
    while remaining > 0:
        requested = min(remaining, 65536)
        payload = audio.readframes(requested)
        if len(payload) == 0 or len(payload) % bytes_per_frame != 0:
            raise AsrTranscriptionError(INVALID_AUDIO)
        observed = len(payload) // bytes_per_frame
        if observed > requested:
            raise AsrTranscriptionError(INVALID_AUDIO)
        remaining -= observed


def _verify_model(path: Path, error_code: str) -> tuple[tuple[str, int, str], ...]:
    try:
        entries = tuple(sorted(path.iterdir(), key=lambda item: item.name))
        for entry in entries:
            if entry.is_dir():
                if entry.name not in _ALLOWED_METADATA_DIRECTORIES or _is_reparse_point(entry):
                    raise AsrTranscriptionError(error_code)
            elif not entry.is_file() or _is_reparse_point(entry):
                raise AsrTranscriptionError(error_code)

        model_files = tuple(entry for entry in entries if entry.is_file())
        expected_names = tuple(sorted(item.file_name for item in PINNED_MODEL_FILES))
        if tuple(item.name for item in model_files) != expected_names:
            raise AsrTranscriptionError(error_code)

        expected_by_name = {item.file_name: item for item in PINNED_MODEL_FILES}
        records: list[str] = []
        snapshot: list[tuple[str, int, str]] = []
        for model_file in model_files:
            expected = expected_by_name[model_file.name]
            byte_size = model_file.stat().st_size
            digest = _sha256(model_file)
            if byte_size != expected.byte_size or digest != expected.sha256:
                raise AsrTranscriptionError(error_code)
            records.append(f"{model_file.name}\0{byte_size}\0{digest}\n")
            snapshot.append((model_file.name, byte_size, digest))

        manifest = hashlib.sha256("".join(records).encode("utf-8")).hexdigest()
        if manifest != MODEL_MANIFEST_SHA256:
            raise AsrTranscriptionError(error_code)
        return tuple(snapshot)
    except AsrTranscriptionError:
        raise
    except OSError:
        raise AsrTranscriptionError(error_code) from None


def _require_canonical_path(
    value: Path,
    *,
    expect_directory: bool,
    error_code: str,
) -> Path:
    if not isinstance(value, Path) or not value.is_absolute():
        raise AsrTranscriptionError(error_code)
    try:
        resolved = value.resolve(strict=True)
        if os.path.normcase(str(resolved)) != os.path.normcase(str(value)):
            raise AsrTranscriptionError(error_code)
        if _is_reparse_point(value):
            raise AsrTranscriptionError(error_code)
        if expect_directory != value.is_dir():
            raise AsrTranscriptionError(error_code)
        if not expect_directory and not value.is_file():
            raise AsrTranscriptionError(error_code)
        return value
    except AsrTranscriptionError:
        raise
    except OSError:
        raise AsrTranscriptionError(error_code) from None


def _is_reparse_point(path: Path) -> bool:
    if path.is_symlink():
        return True
    is_junction = getattr(path, "is_junction", None)
    return bool(is_junction is not None and is_junction())


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(_HASH_CHUNK_BYTES), b""):
            digest.update(chunk)
    return digest.hexdigest()