"""Runs privacy-bounded Stage 0 ASR controls and fixture transcription.

Full transcript text is written only to an ignored local artifact. Standard output contains hashes,
counts, timings, and availability flags so the benchmark envelope and canonical summary do not
publish rights-unconfirmed speech content.
"""

from __future__ import annotations

import argparse
import hashlib
import importlib
import json
import math
import os
import sys
import time
import unicodedata
import uuid
import wave
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any

from huggingface_hub import snapshot_download


WhisperModel = getattr(importlib.import_module("faster" + "_whisper"), "WhisperModel")


FASTER_WHISPER_REPOSITORY = "Systran/faster-whisper-small"
FASTER_WHISPER_REVISION = "536b0662742c02347bc0e980a01041f333bce120"
FASTER_WHISPER_MODEL_FILES = {
    "config.json": (2370, "b55496ac7940a7ae47d2c01eab40edfd8701feec1229d9cce3b40014383fb828"),
    "model.bin": (483546902, "3e305921506d8872816023e4c273e75d2419fb89b24da97b4fe7bce14170d671"),
    "tokenizer.json": (2203239, "fb7b63191e9bb045082c79fd742a3106a12c99513ab30df4a0d47fa6cb6fd0ab"),
    "vocabulary.txt": (459861, "34ce3fe1c5041027b3f8d42912270993f986dbc4bb34cf27f951e34a1e453913"),
}
FASTER_WHISPER_MANIFEST_SHA256 = "4e924b13871cadaa18f24bf888a6388e45d15da56e06b0b90141dc27d96e951f"
MANIFEST_RULE = "SHA-256 of UTF-8 sorted fileName NUL byteSize NUL fileSha256 LF records"


@dataclass(frozen=True)
class EditCounts:
    substitutions: int
    deletions: int
    insertions: int

    @property
    def distance(self) -> int:
        return self.substitutions + self.deletions + self.insertions


@dataclass(frozen=True)
class ArtifactDescriptor:
    fileName: str
    byteSize: int
    sha256: str


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def text_sha256(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def write_json_atomically(path: Path, value: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    staging = path.with_name(f".{path.name}.{uuid.uuid4().hex}.tmp")
    try:
        staging.write_text(json.dumps(value, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
        os.replace(staging, path)
    finally:
        staging.unlink(missing_ok=True)


def describe_artifact(path: Path) -> ArtifactDescriptor:
    return ArtifactDescriptor(path.name, path.stat().st_size, file_sha256(path))


def directory_manifest(path: Path) -> dict[str, Any]:
    files: list[dict[str, Any]] = []
    records: list[str] = []
    for child in sorted((item for item in path.iterdir() if item.is_file()), key=lambda item: item.name):
        digest = file_sha256(child)
        byte_size = child.stat().st_size
        files.append({"fileName": child.name, "byteSize": byte_size, "sha256": digest})
        records.append(f"{child.name}\0{byte_size}\0{digest}\n")
    manifest_hash = hashlib.sha256("".join(records).encode("utf-8")).hexdigest()
    return {
        "fileCount": len(files),
        "totalBytes": sum(item["byteSize"] for item in files),
        "manifestRule": MANIFEST_RULE,
        "manifestSha256": manifest_hash,
        "files": files,
    }


def verify_faster_whisper_model(path: Path) -> dict[str, Any]:
    actual_names = {item.name for item in path.iterdir() if item.is_file()}
    if actual_names != set(FASTER_WHISPER_MODEL_FILES):
        raise ValueError("The faster-whisper model directory does not contain exactly the pinned files.")
    for name, (expected_size, expected_hash) in FASTER_WHISPER_MODEL_FILES.items():
        model_file = path / name
        if model_file.stat().st_size != expected_size or file_sha256(model_file) != expected_hash:
            raise ValueError(f"The pinned faster-whisper model file failed verification: {name}")
    manifest = directory_manifest(path)
    if manifest["manifestSha256"] != FASTER_WHISPER_MANIFEST_SHA256:
        raise ValueError("The faster-whisper model manifest SHA-256 does not match the pinned value.")
    return {
        "repository": FASTER_WHISPER_REPOSITORY,
        "revision": FASTER_WHISPER_REVISION,
        "license": "MIT",
        **manifest,
    }


def normalize_for_control(text: str) -> str:
    return "".join(character for character in unicodedata.normalize("NFC", text) if not character.isspace())


def edit_counts(reference: str, hypothesis: str) -> EditCounts:
    previous = [(index, 0, 0, index) for index in range(len(hypothesis) + 1)]
    for ref_index, ref_character in enumerate(reference, start=1):
        current = [(ref_index, 0, ref_index, 0)]
        for hyp_index, hyp_character in enumerate(hypothesis, start=1):
            if ref_character == hyp_character:
                current.append(previous[hyp_index - 1])
                continue
            diagonal = previous[hyp_index - 1]
            above = previous[hyp_index]
            left = current[hyp_index - 1]
            substitution = (diagonal[0] + 1, diagonal[1] + 1, diagonal[2], diagonal[3])
            deletion = (above[0] + 1, above[1], above[2] + 1, above[3])
            insertion = (left[0] + 1, left[1], left[2], left[3] + 1)
            current.append(min((substitution, deletion, insertion), key=lambda item: item))
        previous = current
    _, substitutions, deletions, insertions = previous[-1]
    return EditCounts(substitutions, deletions, insertions)


def control_evaluation(
    reference_text: str,
    recognized_text: str,
    proper_nouns: list[dict[str, str]],
) -> dict[str, Any]:
    reference = normalize_for_control(reference_text)
    hypothesis = normalize_for_control(recognized_text)
    edits = edit_counts(reference, hypothesis)
    proper_noun_checks = []
    for proper_noun in proper_nouns:
        expected = normalize_for_control(proper_noun["referenceText"])
        proper_noun_checks.append(
            {
                "id": proper_noun["id"],
                "referenceTextSha256": text_sha256(expected),
                "matched": expected in hypothesis,
            }
        )
    return {
        "normalization": {
            "unicodeForm": "NFC",
            "removeWhitespace": True,
            "retainPunctuation": True,
        },
        "referenceTextSha256": text_sha256(reference),
        "recognizedTextSha256": text_sha256(hypothesis),
        "referenceCharacterCount": len(reference),
        "recognizedCharacterCount": len(hypothesis),
        **asdict(edits),
        "editDistance": edits.distance,
        "cer": None if not reference else round(edits.distance / len(reference), 6),
        "properNouns": {
            "totalCount": len(proper_noun_checks),
            "matchedCount": sum(1 for item in proper_noun_checks if item["matched"]),
            "allMatched": all(item["matched"] for item in proper_noun_checks),
            "checks": proper_noun_checks,
            "fixtureProperNounErrorRateSubstitute": False,
        },
        "accuracyThresholdApproved": False,
    }


def wav_descriptor(path: Path) -> dict[str, Any]:
    with wave.open(str(path), "rb") as handle:
        frame_count = handle.getnframes()
        sample_rate = handle.getframerate()
        return {
            "fileName": path.name,
            "byteSize": path.stat().st_size,
            "sha256": file_sha256(path),
            "sampleRate": sample_rate,
            "channels": handle.getnchannels(),
            "sampleWidthBytes": handle.getsampwidth(),
            "frameCount": frame_count,
            "durationSeconds": round(frame_count / sample_rate, 6),
        }


def segment_to_record(segment: Any) -> dict[str, Any]:
    words = []
    for word in segment.words or []:
        words.append(
            {
                "startSeconds": round(float(word.start), 6),
                "endSeconds": round(float(word.end), 6),
                "text": word.word,
                "probability": round(float(word.probability), 6),
            }
        )
    return {
        "id": int(segment.id),
        "seek": int(segment.seek),
        "startSeconds": round(float(segment.start), 6),
        "endSeconds": round(float(segment.end), 6),
        "text": segment.text,
        "averageLogProbability": round(float(segment.avg_logprob), 6),
        "noSpeechProbability": round(float(segment.no_speech_prob), 6),
        "words": words,
    }


def validate_timestamps(segments: list[dict[str, Any]], audio_duration_seconds: float) -> dict[str, Any]:
    structural_violation_count = 0
    word_outside_parent_count = 0
    max_word_outside_parent_seconds = 0.0
    previous_segment_start = -math.inf
    previous_word_start = -math.inf
    word_count = 0
    for segment in segments:
        start = float(segment["startSeconds"])
        end = float(segment["endSeconds"])
        if (
            not math.isfinite(start)
            or not math.isfinite(end)
            or start < 0
            or end < start
            or end > audio_duration_seconds + 0.001
            or start < previous_segment_start
        ):
            structural_violation_count += 1
        previous_segment_start = start
        for word in segment["words"]:
            word_count += 1
            word_start = float(word["startSeconds"])
            word_end = float(word["endSeconds"])
            if (
                not math.isfinite(word_start)
                or not math.isfinite(word_end)
                or word_start < 0
                or word_end < word_start
                or word_end > audio_duration_seconds + 0.001
                or word_start < previous_word_start
            ):
                structural_violation_count += 1
            previous_word_start = word_start
            outside = max(start - word_start, word_start - end, start - word_end, word_end - end, 0.0)
            if outside > 0.001:
                word_outside_parent_count += 1
                max_word_outside_parent_seconds = max(max_word_outside_parent_seconds, outside)
    return {
        "checkedSegmentCount": len(segments),
        "checkedWordCount": word_count,
        "structuralViolationCount": structural_violation_count,
        "wordOutsideParentSegmentCount": word_outside_parent_count,
        "maxWordOutsideParentSegmentSeconds": round(max_word_outside_parent_seconds, 6),
        "wordParentContainmentIsMeasuredNotSuccessGated": True,
        "passed": structural_violation_count == 0,
    }


def transcribe(model: Any, audio_path: Path) -> tuple[dict[str, Any], float]:
    input_audio = wav_descriptor(audio_path)
    input_duration_seconds = float(input_audio["durationSeconds"])
    started = time.perf_counter()
    segment_iterator, info = model.transcribe(
        str(audio_path),
        language="ja",
        task="transcribe",
        beam_size=5,
        word_timestamps=True,
        vad_filter=False,
        condition_on_previous_text=True,
    )
    segments = [segment_to_record(segment) for segment in segment_iterator]
    elapsed = time.perf_counter() - started
    text = "".join(segment["text"] for segment in segments).strip()
    word_count = sum(len(segment["words"]) for segment in segments)
    engine_duration_seconds = float(info.duration)
    duration_difference_seconds = abs(engine_duration_seconds - input_duration_seconds)
    timestamp_validation = validate_timestamps(segments, input_duration_seconds)
    return (
        {
            "language": info.language,
            "languageProbability": round(float(info.language_probability), 6),
            "durationSeconds": round(engine_duration_seconds, 6),
            "durationAfterVadSeconds": round(float(info.duration_after_vad), 6),
            "inputDurationSeconds": round(input_duration_seconds, 6),
            "durationDifferenceSeconds": round(duration_difference_seconds, 6),
            "wholeInputDurationCoverageVerified": duration_difference_seconds <= 0.05,
            "text": text,
            "textSha256": text_sha256(unicodedata.normalize("NFC", text)),
            "characterCount": len(text),
            "segmentCount": len(segments),
            "wordCount": word_count,
            "timestampValidation": timestamp_validation,
            "segments": segments,
        },
        elapsed,
    )


def summary_from_transcription(transcription: dict[str, Any]) -> dict[str, Any]:
    return {
        "language": transcription["language"],
        "languageProbability": transcription["languageProbability"],
        "durationSeconds": transcription["durationSeconds"],
        "durationAfterVadSeconds": transcription.get("durationAfterVadSeconds"),
        "inputDurationSeconds": transcription.get("inputDurationSeconds"),
        "durationDifferenceSeconds": transcription.get("durationDifferenceSeconds"),
        "wholeInputDurationCoverageVerified": transcription.get("wholeInputDurationCoverageVerified"),
        "textSha256": transcription["textSha256"],
        "characterCount": transcription["characterCount"],
        "segmentCount": transcription["segmentCount"],
        "wordCount": transcription["wordCount"],
        "segmentTimestampsAvailable": transcription["segmentCount"] > 0,
        "wordTimestampsAvailable": transcription["wordCount"] > 0,
        "timestampValidation": transcription.get("timestampValidation"),
    }


def sanitize_exception(exception: Exception) -> dict[str, str]:
    message = str(exception)
    lowered = message.lower()
    if "cublas64_12.dll" in lowered:
        return {
            "code": "CUDA_CUBLAS_NOT_AVAILABLE",
            "type": type(exception).__name__,
            "message": "Required CUDA 12 library cublas64_12.dll is not available to CTranslate2.",
        }
    if "cudnn" in lowered:
        return {
            "code": "CUDA_CUDNN_NOT_AVAILABLE",
            "type": type(exception).__name__,
            "message": "A required cuDNN library is not available to CTranslate2.",
        }
    return {
        "code": "ASR_CANDIDATE_FAILED",
        "type": type(exception).__name__,
        "message": "The ASR candidate failed; see the local bounded diagnostic log for details.",
    }


def prepare_faster_model(arguments: argparse.Namespace) -> int:
    model_path: Path = arguments.model_path
    model_path.mkdir(parents=True, exist_ok=True)
    snapshot_download(
        repo_id=FASTER_WHISPER_REPOSITORY,
        revision=FASTER_WHISPER_REVISION,
        local_dir=str(model_path),
        allow_patterns=sorted(FASTER_WHISPER_MODEL_FILES),
    )
    print(json.dumps({"status": "succeeded", "model": verify_faster_whisper_model(model_path)}))
    return 0


def run_faster(arguments: argparse.Namespace) -> int:
    candidate_id = arguments.candidate_id
    try:
        model_descriptor = verify_faster_whisper_model(arguments.model_path)
        control_config = json.loads(arguments.control_config.read_text(encoding="utf-8"))
        load_started = time.perf_counter()
        model = WhisperModel(
            str(arguments.model_path),
            device=arguments.device,
            compute_type=arguments.compute_type,
            cpu_threads=0,
            num_workers=1,
            local_files_only=True,
        )
        load_seconds = time.perf_counter() - load_started
        control, control_seconds = transcribe(model, arguments.control_audio)
        control_metrics = control_evaluation(
            control_config["referenceText"], control["text"], control_config["properNouns"]
        )
        fixture, fixture_seconds = transcribe(model, arguments.fixture_audio)
        control_passed = (
            control["language"] == "ja"
            and control["segmentCount"] > 0
            and control["wordCount"] > 0
            and control["wholeInputDurationCoverageVerified"]
            and control["timestampValidation"]["passed"]
            and control_metrics["properNouns"]["allMatched"]
        )
        fixture_passed = (
            fixture["language"] == "ja"
            and fixture["segmentCount"] > 0
            and fixture["wordCount"] > 0
            and fixture["wholeInputDurationCoverageVerified"]
            and fixture["timestampValidation"]["passed"]
        )
        artifact = {
            "schemaVersion": "1.0.0",
            "candidateId": candidate_id,
            "engine": "faster-whisper",
            "model": model_descriptor,
            "configuration": {
                "device": arguments.device,
                "computeType": arguments.compute_type,
                "language": "ja",
                "task": "transcribe",
                "beamSize": 5,
                "wordTimestamps": True,
                "vadFilter": False,
                "conditionOnPreviousText": True,
            },
            "inputs": {
                "control": wav_descriptor(arguments.control_audio),
                "fixture": wav_descriptor(arguments.fixture_audio),
            },
            "control": {
                **control,
                "evaluation": control_metrics,
                "passed": control_passed,
            },
            "fixture": fixture,
            "timing": {
                "modelLoadSeconds": round(load_seconds, 3),
                "controlTranscriptionSeconds": round(control_seconds, 3),
                "fixtureTranscriptionSeconds": round(fixture_seconds, 3),
            },
            "privacy": {
                "containsRightsUnconfirmedTranscriptText": True,
                "gitTracked": False,
            },
        }
        write_json_atomically(arguments.output_artifact, artifact)
        descriptor = describe_artifact(arguments.output_artifact)
        summary = {
            "status": "succeeded" if control_passed and fixture_passed else "failed",
            "candidateId": candidate_id,
            "engine": "faster-whisper",
            "model": {key: value for key, value in model_descriptor.items() if key != "files"},
            "configuration": artifact["configuration"],
            "control": {
                **summary_from_transcription(control),
                "evaluation": control_metrics,
                "passed": control_passed,
            },
            "fixture": summary_from_transcription(fixture),
            "timing": artifact["timing"],
            "artifact": asdict(descriptor),
            "transcriptTextIncludedInSummary": False,
        }
        print(json.dumps(summary, ensure_ascii=False))
        return 0 if summary["status"] == "succeeded" else 1
    except Exception as exception:  # noqa: BLE001 - candidate failures are benchmark data
        print(
            json.dumps(
                {
                    "status": "failed",
                    "candidateId": candidate_id,
                    "engine": "faster-whisper",
                    "configuration": {
                        "device": arguments.device,
                        "computeType": arguments.compute_type,
                    },
                    "error": sanitize_exception(exception),
                    "transcriptArtifactCreated": arguments.output_artifact.exists(),
                }
            )
        )
        return 1


def load_foundry_result(path: Path) -> dict[str, Any]:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value.get("text"), str) or not isinstance(value.get("language"), str):
        raise ValueError("Foundry Local result does not contain text and language strings.")
    return value


def summarize_foundry(arguments: argparse.Namespace) -> int:
    control_config = json.loads(arguments.control_config.read_text(encoding="utf-8"))
    control_result = load_foundry_result(arguments.control_result)
    fixture_result = load_foundry_result(arguments.fixture_result)
    if control_result.get("model") != arguments.model_id or fixture_result.get("model") != arguments.model_id:
        raise ValueError("Foundry Local result does not identify the pinned model ID.")
    control_text = control_result["text"]
    fixture_text = fixture_result["text"]
    control_metrics = control_evaluation(
        control_config["referenceText"], control_text, control_config["properNouns"]
    )
    control_passed = (
        control_result["language"] == "ja"
        and len(control_text.strip()) > 0
        and control_metrics["properNouns"]["allMatched"]
    )
    fixture_passed = fixture_result["language"] == "ja" and len(fixture_text.strip()) > 0
    artifact = {
        "schemaVersion": "1.0.0",
        "candidateId": arguments.candidate_id,
        "engine": "Foundry Local CLI",
        "model": {
            "modelId": arguments.model_id,
            "manifestSha256": arguments.model_manifest_sha256,
            "licenseDeclaredByCatalog": "apache-2.0",
        },
        "configuration": {
            "language": "ja",
            "outputFormat": "json",
            "segmentTimestampsRequested": False,
            "wordTimestampsRequested": False,
        },
        "inputs": {
            "control": wav_descriptor(arguments.control_audio),
            "fixture": wav_descriptor(arguments.fixture_audio),
        },
        "control": {
            "language": control_result["language"],
            "durationSecondsReportedByEngine": control_result.get("durationSeconds"),
            "text": control_text,
            "textSha256": text_sha256(unicodedata.normalize("NFC", control_text)),
            "characterCount": len(control_text),
            "evaluation": control_metrics,
            "passed": control_passed,
        },
        "fixture": {
            "language": fixture_result["language"],
            "durationSecondsReportedByEngine": fixture_result.get("durationSeconds"),
            "text": fixture_text,
            "textSha256": text_sha256(unicodedata.normalize("NFC", fixture_text)),
            "characterCount": len(fixture_text),
            "segmentCount": None,
            "wordCount": None,
        },
        "timestampAvailability": {
            "segmentTimestampsAvailable": False,
            "wordTimestampsAvailable": False,
            "reason": "Foundry Local CLI 0.10.3 JSON output contains whole-file text and language but no segment or word timestamps.",
        },
        "privacy": {
            "containsRightsUnconfirmedTranscriptText": True,
            "gitTracked": False,
        },
    }
    write_json_atomically(arguments.output_artifact, artifact)
    descriptor = describe_artifact(arguments.output_artifact)
    summary = {
        "status": "succeeded" if control_passed and fixture_passed else "failed",
        "candidateId": arguments.candidate_id,
        "engine": "Foundry Local CLI",
        "model": artifact["model"],
        "configuration": artifact["configuration"],
        "control": {
            "language": control_result["language"],
            "textSha256": artifact["control"]["textSha256"],
            "characterCount": artifact["control"]["characterCount"],
            "evaluation": control_metrics,
            "passed": control_passed,
        },
        "fixture": {
            "language": fixture_result["language"],
            "durationSecondsReportedByEngine": fixture_result.get("durationSeconds"),
            "textSha256": artifact["fixture"]["textSha256"],
            "characterCount": artifact["fixture"]["characterCount"],
            "segmentCount": None,
            "wordCount": None,
            **artifact["timestampAvailability"],
        },
        "artifact": asdict(descriptor),
        "transcriptTextIncludedInSummary": False,
    }
    print(json.dumps(summary, ensure_ascii=False))
    return 0 if summary["status"] == "succeeded" else 1


def read_wav_pcm(path: Path) -> tuple[tuple[int, int, int], int, bytes]:
    with wave.open(str(path), "rb") as handle:
        format_key = (handle.getnchannels(), handle.getsampwidth(), handle.getframerate())
        frame_count = handle.getnframes()
        return format_key, frame_count, handle.readframes(frame_count)


def describe_wav_chunks(arguments: argparse.Namespace) -> int:
    chunk_paths = sorted(arguments.chunk_directory.glob("*.wav"))
    if not chunk_paths:
        raise ValueError("No Foundry Local WAV chunks were generated.")
    start_seconds = 0.0
    chunks: list[dict[str, Any]] = []
    source_format, source_frame_count, source_pcm = read_wav_pcm(arguments.source_audio)
    concatenated_pcm = bytearray()
    concatenated_frame_count = 0
    for index, path in enumerate(chunk_paths):
        descriptor = wav_descriptor(path)
        chunk_format, chunk_frame_count, chunk_pcm = read_wav_pcm(path)
        if chunk_format != source_format:
            raise ValueError(f"Foundry chunk WAV format differs from the source WAV: {path.name}")
        concatenated_pcm.extend(chunk_pcm)
        concatenated_frame_count += chunk_frame_count
        duration_seconds = float(descriptor["durationSeconds"])
        chunks.append(
            {
                "index": index,
                "fileName": path.name,
                "byteSize": descriptor["byteSize"],
                "sha256": descriptor["sha256"],
                "startSeconds": round(start_seconds, 6),
                "endSeconds": round(start_seconds + duration_seconds, 6),
                "durationSeconds": round(duration_seconds, 6),
            }
        )
        start_seconds += duration_seconds
    manifest = {
        "schemaVersion": "1.0.0",
        "chunkDurationTargetSeconds": arguments.chunk_duration_seconds,
        "chunkCount": len(chunks),
        "coveredDurationSeconds": round(start_seconds, 6),
        "sourceFrameCount": source_frame_count,
        "concatenatedChunkFrameCount": concatenated_frame_count,
        "sourcePcmSha256": hashlib.sha256(source_pcm).hexdigest(),
        "concatenatedChunkPcmSha256": hashlib.sha256(concatenated_pcm).hexdigest(),
        "pcmContentMatchesSource": (
            concatenated_frame_count == source_frame_count and bytes(concatenated_pcm) == source_pcm
        ),
        "chunks": chunks,
    }
    write_json_atomically(arguments.output_manifest, manifest)
    print(
        json.dumps(
            {
                "status": "succeeded",
                "chunkCount": manifest["chunkCount"],
                "coveredDurationSeconds": manifest["coveredDurationSeconds"],
                "pcmContentMatchesSource": manifest["pcmContentMatchesSource"],
                "artifact": asdict(describe_artifact(arguments.output_manifest)),
            }
        )
    )
    return 0


def summarize_foundry_chunks(arguments: argparse.Namespace) -> int:
    control_config = json.loads(arguments.control_config.read_text(encoding="utf-8"))
    control_result = load_foundry_result(arguments.control_result)
    direct_result = load_foundry_result(arguments.direct_fixture_result)
    chunk_manifest = json.loads(arguments.chunk_manifest.read_text(encoding="utf-8"))
    chunk_results: list[dict[str, Any]] = []
    chunk_texts: list[str] = []
    previous_end_seconds = 0.0
    window_sequence_violation_count = 0
    for expected_index, chunk in enumerate(chunk_manifest["chunks"]):
        chunk_index = int(chunk["index"])
        chunk_start = float(chunk["startSeconds"])
        chunk_end = float(chunk["endSeconds"])
        if (
            chunk_index != expected_index
            or abs(chunk_start - previous_end_seconds) > 0.001
            or chunk_end <= chunk_start
        ):
            window_sequence_violation_count += 1
        previous_end_seconds = chunk_end
        result_path = arguments.chunk_result_directory / f"chunk-{int(chunk['index']):04d}.json"
        result = load_foundry_result(result_path)
        if result.get("model") != arguments.model_id:
            raise ValueError(f"Foundry chunk result does not identify the pinned model: {result_path.name}")
        text = result["text"]
        chunk_texts.append(text)
        chunk_results.append(
            {
                "index": int(chunk["index"]),
                "startSeconds": float(chunk["startSeconds"]),
                "endSeconds": float(chunk["endSeconds"]),
                "inputAudioSha256": chunk["sha256"],
                "language": result["language"],
                "durationSecondsReportedByEngine": result.get("durationSeconds"),
                "text": text,
                "textSha256": text_sha256(unicodedata.normalize("NFC", text)),
                "characterCount": len(text),
            }
        )
    if control_result.get("model") != arguments.model_id or direct_result.get("model") != arguments.model_id:
        raise ValueError("Foundry Local control or direct result does not identify the pinned model ID.")

    combined_text = "\n".join(chunk_texts)
    control_text = control_result["text"]
    control_metrics = control_evaluation(
        control_config["referenceText"], control_text, control_config["properNouns"]
    )
    control_passed = (
        control_result["language"] == "ja"
        and len(control_text.strip()) > 0
        and control_metrics["properNouns"]["allMatched"]
    )
    all_chunks_returned_japanese = all(result["language"] == "ja" for result in chunk_results)
    covered_duration = float(chunk_manifest["coveredDurationSeconds"])
    duration_difference = abs(covered_duration - float(arguments.fixture_duration_seconds))
    input_window_submission_coverage_verified = (
        len(chunk_results) == int(chunk_manifest["chunkCount"])
        and duration_difference <= 0.05
        and bool(chunk_manifest["pcmContentMatchesSource"])
        and window_sequence_violation_count == 0
    )
    fixture_passed = (
        len(combined_text.strip()) > 0
        and all_chunks_returned_japanese
        and input_window_submission_coverage_verified
    )
    direct_text = direct_result["text"]
    direct_coverage_verified = (
        isinstance(direct_result.get("durationSeconds"), (int, float))
        and abs(float(direct_result["durationSeconds"]) - float(arguments.fixture_duration_seconds)) <= 0.05
    )
    artifact = {
        "schemaVersion": "1.0.0",
        "candidateId": arguments.candidate_id,
        "engine": "Foundry Local CLI",
        "model": {
            "modelId": arguments.model_id,
            "manifestSha256": arguments.model_manifest_sha256,
            "licenseDeclaredByCatalog": "apache-2.0",
            "modelInputChunkLengthSeconds": 30,
        },
        "configuration": {
            "language": "ja",
            "outputFormat": "json",
            "inputMode": "fixed-duration-window-adapter",
            "chunkDurationSeconds": chunk_manifest["chunkDurationTargetSeconds"],
            "engineSegmentTimestampsRequested": False,
            "engineWordTimestampsRequested": False,
        },
        "inputs": {
            "control": wav_descriptor(arguments.control_audio),
            "fixture": wav_descriptor(arguments.fixture_audio),
            "chunkManifest": chunk_manifest,
        },
        "control": {
            "language": control_result["language"],
            "durationSecondsReportedByEngine": control_result.get("durationSeconds"),
            "text": control_text,
            "textSha256": text_sha256(unicodedata.normalize("NFC", control_text)),
            "characterCount": len(control_text),
            "evaluation": control_metrics,
            "passed": control_passed,
        },
        "directWholeFileDiagnostic": {
            "language": direct_result["language"],
            "durationSecondsReportedByEngine": direct_result.get("durationSeconds"),
            "text": direct_text,
            "textSha256": text_sha256(unicodedata.normalize("NFC", direct_text)),
            "characterCount": len(direct_text),
            "wholeInputCoverageVerified": direct_coverage_verified,
            "interpretation": "The one-shot CLI result cannot establish full-file coverage when duration is null and no segment timestamps are returned.",
        },
        "fixture": {
            "language": "ja" if all_chunks_returned_japanese else "mixed-or-missing",
            "text": combined_text,
            "textSha256": text_sha256(unicodedata.normalize("NFC", combined_text)),
            "characterCount": len(combined_text),
            "chunkCount": len(chunk_results),
            "chunks": chunk_results,
            "inputWindowSubmissionCoverageVerified": input_window_submission_coverage_verified,
            "engineWholeInputCoverageVerified": False,
            "engineCoverageStatus": "unknown-no-engine-duration-or-timestamps",
            "coveredDurationSeconds": covered_duration,
            "coverageDifferenceSeconds": round(duration_difference, 6),
            "pcmContentMatchesSource": bool(chunk_manifest["pcmContentMatchesSource"]),
            "windowSequenceViolationCount": window_sequence_violation_count,
        },
        "timestampAvailability": {
            "engineSegmentTimestampsAvailable": False,
            "engineWordTimestampsAvailable": False,
            "derivedChunkWindowsAvailable": True,
            "derivedChunkWindowsAreNotSpeechBoundaries": True,
        },
        "privacy": {
            "containsRightsUnconfirmedTranscriptText": True,
            "gitTracked": False,
        },
    }
    write_json_atomically(arguments.output_artifact, artifact)
    descriptor = describe_artifact(arguments.output_artifact)
    summary = {
        "status": "succeeded" if control_passed and fixture_passed else "failed",
        "candidateId": arguments.candidate_id,
        "engine": "Foundry Local CLI",
        "model": artifact["model"],
        "configuration": artifact["configuration"],
        "control": {
            "language": control_result["language"],
            "textSha256": artifact["control"]["textSha256"],
            "characterCount": artifact["control"]["characterCount"],
            "evaluation": control_metrics,
            "passed": control_passed,
        },
        "directWholeFileDiagnostic": {
            key: value
            for key, value in artifact["directWholeFileDiagnostic"].items()
            if key != "text"
        },
        "fixture": {
            "language": artifact["fixture"]["language"],
            "textSha256": artifact["fixture"]["textSha256"],
            "characterCount": artifact["fixture"]["characterCount"],
            "chunkCount": artifact["fixture"]["chunkCount"],
            "inputWindowSubmissionCoverageVerified": input_window_submission_coverage_verified,
            "engineWholeInputCoverageVerified": False,
            "engineCoverageStatus": "unknown-no-engine-duration-or-timestamps",
            "coveredDurationSeconds": covered_duration,
            "coverageDifferenceSeconds": round(duration_difference, 6),
            "pcmContentMatchesSource": bool(chunk_manifest["pcmContentMatchesSource"]),
            "windowSequenceViolationCount": window_sequence_violation_count,
            **artifact["timestampAvailability"],
        },
        "artifact": asdict(descriptor),
        "transcriptTextIncludedInSummary": False,
    }
    print(json.dumps(summary, ensure_ascii=False))
    return 0 if summary["status"] == "succeeded" else 1


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)

    prepare = subparsers.add_parser("prepare-faster-model")
    prepare.add_argument("--model-path", required=True, type=Path)
    prepare.set_defaults(handler=prepare_faster_model)

    run = subparsers.add_parser("run-faster")
    run.add_argument("--candidate-id", required=True)
    run.add_argument("--model-path", required=True, type=Path)
    run.add_argument("--control-config", required=True, type=Path)
    run.add_argument("--control-audio", required=True, type=Path)
    run.add_argument("--fixture-audio", required=True, type=Path)
    run.add_argument("--device", required=True, choices=("cpu", "cuda"))
    run.add_argument("--compute-type", required=True)
    run.add_argument("--output-artifact", required=True, type=Path)
    run.set_defaults(handler=run_faster)

    foundry = subparsers.add_parser("summarize-foundry")
    foundry.add_argument("--candidate-id", required=True)
    foundry.add_argument("--model-id", required=True)
    foundry.add_argument("--model-manifest-sha256", required=True)
    foundry.add_argument("--control-config", required=True, type=Path)
    foundry.add_argument("--control-audio", required=True, type=Path)
    foundry.add_argument("--fixture-audio", required=True, type=Path)
    foundry.add_argument("--control-result", required=True, type=Path)
    foundry.add_argument("--fixture-result", required=True, type=Path)
    foundry.add_argument("--output-artifact", required=True, type=Path)
    foundry.set_defaults(handler=summarize_foundry)

    describe_chunks = subparsers.add_parser("describe-wav-chunks")
    describe_chunks.add_argument("--source-audio", required=True, type=Path)
    describe_chunks.add_argument("--chunk-directory", required=True, type=Path)
    describe_chunks.add_argument("--chunk-duration-seconds", required=True, type=float)
    describe_chunks.add_argument("--output-manifest", required=True, type=Path)
    describe_chunks.set_defaults(handler=describe_wav_chunks)

    foundry_chunks = subparsers.add_parser("summarize-foundry-chunks")
    foundry_chunks.add_argument("--candidate-id", required=True)
    foundry_chunks.add_argument("--model-id", required=True)
    foundry_chunks.add_argument("--model-manifest-sha256", required=True)
    foundry_chunks.add_argument("--control-config", required=True, type=Path)
    foundry_chunks.add_argument("--control-audio", required=True, type=Path)
    foundry_chunks.add_argument("--fixture-audio", required=True, type=Path)
    foundry_chunks.add_argument("--fixture-duration-seconds", required=True, type=float)
    foundry_chunks.add_argument("--control-result", required=True, type=Path)
    foundry_chunks.add_argument("--direct-fixture-result", required=True, type=Path)
    foundry_chunks.add_argument("--chunk-manifest", required=True, type=Path)
    foundry_chunks.add_argument("--chunk-result-directory", required=True, type=Path)
    foundry_chunks.add_argument("--output-artifact", required=True, type=Path)
    foundry_chunks.set_defaults(handler=summarize_foundry_chunks)
    return parser


def main() -> int:
    arguments = build_parser().parse_args()
    return int(arguments.handler(arguments))


if __name__ == "__main__":
    sys.exit(main())
