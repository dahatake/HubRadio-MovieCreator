"""Evaluates timestamp structure and cross-device agreement without publishing transcript text.

The tracked synthetic control places common Japanese terms in independently constructed audio
slots. It demonstrates coarse slot assignment only. Fixture timing accuracy remains unmeasured
until an independently authored transcript reference with boundaries exists.
"""

from __future__ import annotations

import argparse
import difflib
import hashlib
import json
import math
import os
import statistics
import sys
import unicodedata
import uuid
import wave
from pathlib import Path
from typing import Any, Iterable

import asr_benchmark as asr


PARENT_CONTAINMENT_OBSERVATION_THRESHOLD_SECONDS = 0.001
PAIRING_DIAGNOSTIC_THRESHOLDS_SECONDS = (0.1, 0.5, 1.0)


def read_json_object(path: Path) -> dict[str, Any]:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError(f"Expected a JSON object: {path.name}")
    return value


def write_json_atomically(path: Path, value: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    staging = path.with_name(f".{path.name}.{uuid.uuid4().hex}.tmp")
    try:
        staging.write_text(json.dumps(value, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
        os.replace(staging, path)
    finally:
        staging.unlink(missing_ok=True)


def write_wave_atomically(
    path: Path,
    channels: int,
    sample_width_bytes: int,
    sample_rate: int,
    pcm: bytes,
) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    staging = path.with_name(f".{path.name}.{uuid.uuid4().hex}.tmp")
    try:
        with wave.open(str(staging), "wb") as handle:
            handle.setnchannels(channels)
            handle.setsampwidth(sample_width_bytes)
            handle.setframerate(sample_rate)
            handle.writeframes(pcm)
        os.replace(staging, path)
    finally:
        staging.unlink(missing_ok=True)


def normalized_text(value: str) -> str:
    return "".join(character for character in unicodedata.normalize("NFC", value) if not character.isspace())


def text_sha256(value: str) -> str:
    return hashlib.sha256(value.encode("utf-8")).hexdigest()


def file_descriptor(path: Path) -> dict[str, Any]:
    return {
        "fileName": path.name,
        "byteSize": path.stat().st_size,
        "sha256": asr.file_sha256(path),
    }


def seconds_to_frames(value: Any, sample_rate: int) -> int:
    seconds = float(value)
    frames = seconds * sample_rate
    rounded = round(frames)
    if seconds < 0 or not math.isclose(frames, rounded, abs_tol=1e-9):
        raise ValueError("Control silence durations must map exactly to whole audio frames.")
    return int(rounded)


def read_wave(path: Path) -> tuple[tuple[int, int, int], int, bytes]:
    with wave.open(str(path), "rb") as handle:
        frame_count = handle.getnframes()
        return (
            (handle.getnchannels(), handle.getsampwidth(), handle.getframerate()),
            frame_count,
            handle.readframes(frame_count),
        )


def build_control(arguments: argparse.Namespace) -> int:
    config = read_json_object(arguments.config)
    construction = config["construction"]
    sample_rate = int(construction["sampleRate"])
    channels = int(construction["channels"])
    sample_width = int(construction["sampleWidthBytes"])
    expected_format = (channels, sample_width, sample_rate)
    bytes_per_frame = channels * sample_width

    initial_frames = seconds_to_frames(construction["initialSilenceSeconds"], sample_rate)
    lead_frames = seconds_to_frames(construction["slotLeadSilenceSeconds"], sample_rate)
    trail_frames = seconds_to_frames(construction["slotTrailSilenceSeconds"], sample_rate)
    inter_frames = seconds_to_frames(construction["interSlotSilenceSeconds"], sample_rate)
    final_frames = seconds_to_frames(construction["finalSilenceSeconds"], sample_rate)
    silence_frame = b"\x00" * bytes_per_frame

    pcm = bytearray(silence_frame * initial_frames)
    current_frame = initial_frames
    slots: list[dict[str, Any]] = []
    phrases = config["phrases"]
    if not isinstance(phrases, list) or not phrases:
        raise ValueError("Alignment control must contain at least one phrase.")

    for index, phrase in enumerate(phrases):
        phrase_id = str(phrase["id"])
        clip_path = arguments.clip_directory / f"{phrase_id}.wav"
        clip_format, clip_frames, clip_pcm = read_wave(clip_path)
        if clip_format != expected_format:
            raise ValueError(f"Alignment control clip format differs: {clip_path.name}")

        slot_start_frame = current_frame
        pcm.extend(silence_frame * lead_frames)
        current_frame += lead_frames
        speech_clip_start_frame = current_frame
        pcm.extend(clip_pcm)
        current_frame += clip_frames
        speech_clip_end_frame = current_frame
        pcm.extend(silence_frame * trail_frames)
        current_frame += trail_frames
        slot_end_frame = current_frame

        slots.append(
            {
                "index": index,
                "id": phrase_id,
                "referenceTermSha256": text_sha256(normalized_text(str(phrase["referenceTerm"]))),
                "clip": file_descriptor(clip_path),
                "slotStartSeconds": round(slot_start_frame / sample_rate, 6),
                "slotEndSeconds": round(slot_end_frame / sample_rate, 6),
                "speechClipStartSeconds": round(speech_clip_start_frame / sample_rate, 6),
                "speechClipEndSeconds": round(speech_clip_end_frame / sample_rate, 6),
            }
        )
        if index < len(phrases) - 1:
            pcm.extend(silence_frame * inter_frames)
            current_frame += inter_frames

    pcm.extend(silence_frame * final_frames)
    current_frame += final_frames
    write_wave_atomically(arguments.output_audio, channels, sample_width, sample_rate, bytes(pcm))
    audio = asr.wav_descriptor(arguments.output_audio)
    if int(audio["frameCount"]) != current_frame:
        raise ValueError("Constructed control frame count does not match the manifest calculation.")

    manifest = {
        "schemaVersion": "1.0.0",
        "controlId": config["controlId"],
        "configSha256": asr.file_sha256(arguments.config),
        "construction": {
            "sampleRate": sample_rate,
            "channels": channels,
            "sampleWidthBytes": sample_width,
            "initialSilenceFrames": initial_frames,
            "slotLeadSilenceFrames": lead_frames,
            "slotTrailSilenceFrames": trail_frames,
            "interSlotSilenceFrames": inter_frames,
            "finalSilenceFrames": final_frames,
        },
        "audio": audio,
        "slotCount": len(slots),
        "slots": slots,
        "accuracyThresholdApproved": False,
        "interpretation": "Slot containment is a coarse mechanism control, not word-boundary accuracy ground truth.",
    }
    write_json_atomically(arguments.output_manifest, manifest)
    print(
        json.dumps(
            {
                "status": "succeeded",
                "controlId": config["controlId"],
                "slotCount": len(slots),
                "audio": audio,
                "manifest": file_descriptor(arguments.output_manifest),
            }
        )
    )
    return 0


def iter_words(transcription: dict[str, Any]) -> Iterable[tuple[int, int, dict[str, Any]]]:
    for segment_index, segment in enumerate(transcription["segments"]):
        for word_index, word in enumerate(segment["words"]):
            yield segment_index, word_index, word


def indexed_normalized_words(transcription: dict[str, Any]) -> tuple[str, list[dict[str, Any]]]:
    concatenated: list[str] = []
    indexed: list[dict[str, Any]] = []
    character_offset = 0
    for segment_index, word_index, word in iter_words(transcription):
        token = normalized_text(str(word["text"]))
        if not token:
            continue
        start_offset = character_offset
        character_offset += len(token)
        concatenated.append(token)
        indexed.append(
            {
                "segmentIndex": segment_index,
                "wordIndex": word_index,
                "normalizedToken": token,
                "characterStart": start_offset,
                "characterEnd": character_offset,
                "startSeconds": float(word["startSeconds"]),
                "endSeconds": float(word["endSeconds"]),
            }
        )
    return "".join(concatenated), indexed


def occurrence_offsets(text: str, term: str) -> list[int]:
    offsets: list[int] = []
    start = 0
    while True:
        offset = text.find(term, start)
        if offset < 0:
            return offsets
        offsets.append(offset)
        start = offset + 1


def run_control(arguments: argparse.Namespace) -> int:
    config = read_json_object(arguments.config)
    manifest = read_json_object(arguments.manifest)
    if manifest["configSha256"] != asr.file_sha256(arguments.config):
        raise ValueError("Alignment control configuration hash does not match its manifest.")
    if manifest["audio"]["sha256"] != asr.file_sha256(arguments.control_audio):
        raise ValueError("Alignment control audio hash does not match its manifest.")

    model_descriptor = asr.verify_faster_whisper_model(arguments.model_path)
    model = asr.WhisperModel(
        str(arguments.model_path),
        device=arguments.device,
        compute_type=arguments.compute_type,
        cpu_threads=0,
        num_workers=1,
        local_files_only=True,
    )
    slots_by_id = {str(slot["id"]): slot for slot in manifest["slots"]}
    checks: list[dict[str, Any]] = []
    slot_transcriptions: list[dict[str, Any]] = []
    transcription_seconds = 0.0
    for phrase in config["phrases"]:
        phrase_id = str(phrase["id"])
        slot = slots_by_id[phrase_id]
        clip_path = arguments.clip_directory / f"{phrase_id}.wav"
        if asr.file_sha256(clip_path) != slot["clip"]["sha256"]:
            raise ValueError(f"Alignment slot clip hash mismatch: {phrase_id}")
        clip_descriptor = asr.wav_descriptor(clip_path)
        if (
            int(clip_descriptor["byteSize"]) != int(slot["clip"]["byteSize"])
            or abs(
                float(slot["speechClipEndSeconds"])
                - float(slot["speechClipStartSeconds"])
                - float(clip_descriptor["durationSeconds"])
            )
            > 0.000001
        ):
            raise ValueError(f"Alignment slot clip duration or size mismatch: {phrase_id}")
        transcription, elapsed_seconds = asr.transcribe(model, clip_path)
        if (
            transcription["wholeInputDurationCoverageVerified"] is not True
            or transcription["textSha256"] == ""
            or abs(
                float(transcription["inputDurationSeconds"])
                - float(clip_descriptor["durationSeconds"])
            )
            > 0.000001
        ):
            raise ValueError(f"Alignment slot transcription input mismatch: {phrase_id}")
        transcription_seconds += elapsed_seconds
        normalized_output, words = indexed_normalized_words(transcription)
        term = normalized_text(str(phrase["referenceTerm"]))
        offsets = occurrence_offsets(normalized_output, term)
        mapped_occurrences: list[dict[str, Any]] = []
        for term_start in offsets:
            term_end = term_start + len(term)
            occurrence_words = [
                word
                for word in words
                if word["characterStart"] < term_end and word["characterEnd"] > term_start
            ]
            local_start = min((word["startSeconds"] for word in occurrence_words), default=None)
            local_end = max((word["endSeconds"] for word in occurrence_words), default=None)
            mapped_occurrences.append(
                {
                    "mappedWordCount": len(occurrence_words),
                    "observedStartSeconds": (
                        None
                        if local_start is None
                        else float(slot["speechClipStartSeconds"]) + local_start
                    ),
                    "observedEndSeconds": (
                        None
                        if local_end is None
                        else float(slot["speechClipStartSeconds"]) + local_end
                    ),
                }
            )
        assigned_occurrences = [
            occurrence
            for occurrence in mapped_occurrences
            if occurrence["observedStartSeconds"] is not None
            and occurrence["observedEndSeconds"] is not None
            and occurrence["observedStartSeconds"] >= float(slot["slotStartSeconds"])
            and occurrence["observedEndSeconds"] <= float(slot["slotEndSeconds"])
        ]
        selected = assigned_occurrences[0] if len(assigned_occurrences) == 1 else None
        within_speech_clip_range = (
            selected is not None
            and selected["observedStartSeconds"] >= float(slot["speechClipStartSeconds"])
            and selected["observedEndSeconds"] <= float(slot["speechClipEndSeconds"])
        )
        checks.append(
            {
                "id": phrase_id,
                "referenceTermSha256": text_sha256(term),
                "occurrenceCount": len(offsets),
                "assignedSlotOccurrenceCount": len(assigned_occurrences),
                "mappedWordCount": 0 if selected is None else selected["mappedWordCount"],
                "slotStartSeconds": slot["slotStartSeconds"],
                "slotEndSeconds": slot["slotEndSeconds"],
                "speechClipStartSeconds": slot["speechClipStartSeconds"],
                "speechClipEndSeconds": slot["speechClipEndSeconds"],
                "observedStartSeconds": None if selected is None else round(selected["observedStartSeconds"], 6),
                "observedEndSeconds": None if selected is None else round(selected["observedEndSeconds"], 6),
                "withinAssignedSlot": len(assigned_occurrences) == 1,
                "withinSpeechClipRange": within_speech_clip_range,
            }
        )
        slot_transcriptions.append(
            {
                "id": phrase_id,
                "clip": clip_descriptor,
                "globalSpeechClipStartSeconds": slot["speechClipStartSeconds"],
                "transcription": transcription,
            }
        )

    aggregate_timestamp_validation = {
        "checkedSegmentCount": sum(
            int(item["transcription"]["timestampValidation"]["checkedSegmentCount"])
            for item in slot_transcriptions
        ),
        "checkedWordCount": sum(
            int(item["transcription"]["timestampValidation"]["checkedWordCount"])
            for item in slot_transcriptions
        ),
        "structuralViolationCount": sum(
            int(item["transcription"]["timestampValidation"]["structuralViolationCount"])
            for item in slot_transcriptions
        ),
        "wordOutsideParentSegmentCount": sum(
            int(item["transcription"]["timestampValidation"]["wordOutsideParentSegmentCount"])
            for item in slot_transcriptions
        ),
        "maxWordOutsideParentSegmentSeconds": max(
            (
                float(item["transcription"]["timestampValidation"]["maxWordOutsideParentSegmentSeconds"])
                for item in slot_transcriptions
            ),
            default=0.0,
        ),
        "wordParentContainmentIsMeasuredNotSuccessGated": True,
        "passed": all(
            bool(item["transcription"]["timestampValidation"]["passed"])
            for item in slot_transcriptions
        ),
    }
    all_japanese = all(item["transcription"]["language"] == "ja" for item in slot_transcriptions)

    passed = (
        all_japanese
        and aggregate_timestamp_validation["passed"]
        and len(checks) == int(manifest["slotCount"])
        and all(check["assignedSlotOccurrenceCount"] == 1 for check in checks)
        and all(check["mappedWordCount"] > 0 for check in checks)
        and all(check["withinAssignedSlot"] for check in checks)
        and all(check["withinSpeechClipRange"] for check in checks)
    )
    artifact = {
        "schemaVersion": "1.0.0",
        "candidateId": arguments.candidate_id,
        "model": model_descriptor,
        "configuration": {
            "device": arguments.device,
            "computeType": arguments.compute_type,
            "language": "ja",
            "beamSize": 5,
            "wordTimestamps": True,
            "vadFilter": False,
            "conditionOnPreviousText": True,
        },
        "controlManifestSha256": asr.file_sha256(arguments.manifest),
        "constructedControlAudio": asr.wav_descriptor(arguments.control_audio),
        "slotTranscriptions": slot_transcriptions,
        "termWindowChecks": checks,
        "passed": passed,
        "interpretation": "Term-window containment is a coarse slot-assignment control, not exact word-boundary accuracy.",
    }
    write_json_atomically(arguments.output_artifact, artifact)
    summary = {
        "status": "succeeded" if passed else "failed",
        "candidateId": arguments.candidate_id,
        "model": {key: value for key, value in model_descriptor.items() if key != "files"},
        "configuration": artifact["configuration"],
        "transcriptionInputMode": "independent-slot-clips-mapped-through-manifest-offsets",
        "constructedControlAudioWasTranscribed": False,
        "controlAudioSha256": manifest["audio"]["sha256"],
        "controlDurationSeconds": manifest["audio"]["durationSeconds"],
        "language": "ja" if all_japanese else "mixed-or-missing",
        "minimumLanguageProbability": min(
            float(item["transcription"]["languageProbability"]) for item in slot_transcriptions
        ),
        "recognizedTextSetSha256": text_sha256(
            "\n".join(item["transcription"]["text"] for item in slot_transcriptions)
        ),
        "recognizedCharacterCount": sum(
            int(item["transcription"]["characterCount"]) for item in slot_transcriptions
        ),
        "segmentCount": sum(int(item["transcription"]["segmentCount"]) for item in slot_transcriptions),
        "wordCount": sum(int(item["transcription"]["wordCount"]) for item in slot_transcriptions),
        "timestampValidation": aggregate_timestamp_validation,
        "termWindowChecks": checks,
        "allAssignedSlotsContainExactlyOneMappedTerm": all(
            check["assignedSlotOccurrenceCount"] == 1 for check in checks
        ),
        "allMappedTermsWithinAssignedSlots": all(check["withinAssignedSlot"] for check in checks),
        "allMappedTermsWithinSpeechClipRanges": all(
            check["withinSpeechClipRange"] for check in checks
        ),
        "transcriptionMilliseconds": round(transcription_seconds * 1000, 3),
        "accuracyThresholdApproved": False,
        "exactBoundaryAccuracyMeasured": False,
        "artifact": file_descriptor(arguments.output_artifact),
        "transcriptTextIncludedInSummary": False,
    }
    print(json.dumps(summary, ensure_ascii=False))
    return 0 if passed else 1


def percentile(values: list[float], probability: float) -> float | None:
    if not values:
        return None
    ordered = sorted(values)
    if len(ordered) == 1:
        return round(ordered[0], 6)
    rank = (len(ordered) - 1) * probability
    lower = math.floor(rank)
    upper = math.ceil(rank)
    if lower == upper:
        return round(ordered[lower], 6)
    fraction = rank - lower
    return round(ordered[lower] * (1 - fraction) + ordered[upper] * fraction, 6)


def distribution(values: list[float]) -> dict[str, Any]:
    return {
        "count": len(values),
        "minimum": None if not values else round(min(values), 6),
        "median": percentile(values, 0.5),
        "p90": percentile(values, 0.9),
        "p99": percentile(values, 0.99),
        "maximum": None if not values else round(max(values), 6),
        "mean": None if not values else round(statistics.fmean(values), 6),
        "percentileMethod": "linear interpolation over sorted observations",
    }


def analyze_candidate(artifact: dict[str, Any], expected_id: str) -> tuple[dict[str, Any], list[dict[str, Any]]]:
    if artifact.get("candidateId") != expected_id or artifact.get("engine") != "faster-whisper":
        raise ValueError(f"Unexpected ASR artifact candidate: {expected_id}")
    fixture = artifact["fixture"]
    duration = float(fixture["inputDurationSeconds"])
    segment_violations = 0
    word_violations = 0
    parent_overflows: list[float] = []
    previous_segment_start = -math.inf
    previous_word_start = -math.inf
    words: list[dict[str, Any]] = []
    probabilities: list[float] = []
    for segment_index, segment in enumerate(fixture["segments"]):
        segment_start = float(segment["startSeconds"])
        segment_end = float(segment["endSeconds"])
        if (
            not math.isfinite(segment_start)
            or not math.isfinite(segment_end)
            or segment_start < 0
            or segment_end < segment_start
            or segment_end > duration + 0.001
            or segment_start < previous_segment_start
        ):
            segment_violations += 1
        previous_segment_start = segment_start
        for word_index, word in enumerate(segment["words"]):
            token = normalized_text(str(word["text"]))
            word_start = float(word["startSeconds"])
            word_end = float(word["endSeconds"])
            probability = float(word["probability"])
            if (
                not token
                or not math.isfinite(word_start)
                or not math.isfinite(word_end)
                or word_start < 0
                or word_end < word_start
                or word_end > duration + 0.001
                or word_start < previous_word_start
            ):
                word_violations += 1
            previous_word_start = word_start
            overflow = max(segment_start - word_start, word_end - segment_end, 0.0)
            if overflow > PARENT_CONTAINMENT_OBSERVATION_THRESHOLD_SECONDS:
                parent_overflows.append(overflow)
            words.append(
                {
                    "segmentIndex": segment_index,
                    "wordIndex": word_index,
                    "normalizedToken": token,
                    "tokenSha256": text_sha256(token),
                    "startSeconds": word_start,
                    "endSeconds": word_end,
                    "probability": probability,
                }
            )
            probabilities.append(probability)

    summary = {
        "candidateId": expected_id,
        "fixtureTextSha256": fixture["textSha256"],
        "inputAudioSha256": artifact["inputs"]["fixture"]["sha256"],
        "inputDurationSeconds": duration,
        "segmentCount": len(fixture["segments"]),
        "wordCount": len(words),
        "emittedWordsWithFiniteTimestamps": sum(
            1
            for word in words
            if math.isfinite(word["startSeconds"]) and math.isfinite(word["endSeconds"])
        ),
        "emittedWordTimestampCoverage": (
            None if not words else round(sum(1 for word in words if math.isfinite(word["startSeconds"]) and math.isfinite(word["endSeconds"])) / len(words), 6)
        ),
        "segmentStructuralViolationCount": segment_violations,
        "wordStructuralViolationCount": word_violations,
        "wordOutsideParentSegmentCount": len(parent_overflows),
        "parentContainmentObservationThresholdSeconds": PARENT_CONTAINMENT_OBSERVATION_THRESHOLD_SECONDS,
        "parentSegmentOverflowSeconds": distribution(parent_overflows),
        "wordProbability": distribution(probabilities),
        "exactBoundaryAccuracyMeasured": False,
        "humanTimingReferenceAvailable": False,
    }
    return summary, words


def analyze_fixture(arguments: argparse.Namespace) -> int:
    cpu_path = arguments.cpu_artifact
    cuda_path = arguments.cuda_artifact
    if asr.file_sha256(cpu_path) != arguments.cpu_artifact_sha256:
        raise ValueError("CPU ASR artifact hash mismatch.")
    if asr.file_sha256(cuda_path) != arguments.cuda_artifact_sha256:
        raise ValueError("CUDA ASR artifact hash mismatch.")
    cpu_artifact = read_json_object(cpu_path)
    cuda_artifact = read_json_object(cuda_path)
    cpu_summary, cpu_words = analyze_candidate(cpu_artifact, "faster-whisper-small-cpu-int8")
    cuda_summary, cuda_words = analyze_candidate(cuda_artifact, "faster-whisper-small-cuda-float16")
    if cpu_summary["inputAudioSha256"] != cuda_summary["inputAudioSha256"]:
        raise ValueError("CPU and CUDA ASR artifacts do not describe the same normalized audio.")
    if cpu_summary["inputAudioSha256"] != arguments.input_audio_sha256:
        raise ValueError("ASR artifacts do not match the expected normalized audio hash.")

    cpu_tokens = [word["normalizedToken"] for word in cpu_words]
    cuda_tokens = [word["normalizedToken"] for word in cuda_words]
    matcher = difflib.SequenceMatcher(a=cpu_tokens, b=cuda_tokens, autojunk=False)
    matched_pairs: list[dict[str, Any]] = []
    start_differences: list[float] = []
    end_differences: list[float] = []
    midpoint_differences: list[float] = []
    duration_differences: list[float] = []
    for block in matcher.get_matching_blocks():
        for offset in range(block.size):
            cpu_word = cpu_words[block.a + offset]
            cuda_word = cuda_words[block.b + offset]
            start_difference = abs(cpu_word["startSeconds"] - cuda_word["startSeconds"])
            end_difference = abs(cpu_word["endSeconds"] - cuda_word["endSeconds"])
            cpu_midpoint = (cpu_word["startSeconds"] + cpu_word["endSeconds"]) / 2
            cuda_midpoint = (cuda_word["startSeconds"] + cuda_word["endSeconds"]) / 2
            midpoint_difference = abs(cpu_midpoint - cuda_midpoint)
            duration_difference = abs(
                (cpu_word["endSeconds"] - cpu_word["startSeconds"])
                - (cuda_word["endSeconds"] - cuda_word["startSeconds"])
            )
            start_differences.append(start_difference)
            end_differences.append(end_difference)
            midpoint_differences.append(midpoint_difference)
            duration_differences.append(duration_difference)
            matched_pairs.append(
                {
                    "tokenSha256": cpu_word["tokenSha256"],
                    "cpuWordIndex": block.a + offset,
                    "cudaWordIndex": block.b + offset,
                    "cpuStartSeconds": round(cpu_word["startSeconds"], 6),
                    "cpuEndSeconds": round(cpu_word["endSeconds"], 6),
                    "cudaStartSeconds": round(cuda_word["startSeconds"], 6),
                    "cudaEndSeconds": round(cuda_word["endSeconds"], 6),
                }
            )

    agreement = {
        "algorithm": "Python difflib.SequenceMatcher over NFC whitespace-free emitted word tokens with autojunk disabled",
        "matchedExactTokenCount": len(matched_pairs),
        "cpuWordCount": len(cpu_words),
        "cudaWordCount": len(cuda_words),
        "matchedShareOfCpuWords": None if not cpu_words else round(len(matched_pairs) / len(cpu_words), 6),
        "matchedShareOfCudaWords": None if not cuda_words else round(len(matched_pairs) / len(cuda_words), 6),
        "startAbsoluteDifferenceSeconds": distribution(start_differences),
        "endAbsoluteDifferenceSeconds": distribution(end_differences),
        "midpointAbsoluteDifferenceSeconds": distribution(midpoint_differences),
        "durationAbsoluteDifferenceSeconds": distribution(duration_differences),
        "startDifferenceDiagnosticCounts": {
            "over0.1Seconds": sum(
                value > PAIRING_DIAGNOSTIC_THRESHOLDS_SECONDS[0] for value in start_differences
            ),
            "over0.5Seconds": sum(
                value > PAIRING_DIAGNOSTIC_THRESHOLDS_SECONDS[1] for value in start_differences
            ),
            "over1.0Seconds": sum(
                value > PAIRING_DIAGNOSTIC_THRESHOLDS_SECONDS[2] for value in start_differences
            ),
            "thresholdsAreDescriptiveNotAcceptanceLimits": True,
        },
        "textHashesIdentical": cpu_summary["fixtureTextSha256"] == cuda_summary["fixtureTextSha256"],
        "duplicateTokenPairingAmbiguityPossible": True,
        "agreementIsAccuracyEvidence": False,
        "interpretation": "Agreement between two compute configurations of one model is descriptive and cannot establish correctness without independent timing annotation.",
    }
    artifact = {
        "schemaVersion": "1.0.0",
        "inputAudioSha256": arguments.input_audio_sha256,
        "candidates": [cpu_summary, cuda_summary],
        "agreement": agreement,
        "matchedPairs": matched_pairs,
        "transcriptTextIncluded": False,
    }
    write_json_atomically(arguments.output_artifact, artifact)
    print(
        json.dumps(
            {
                "status": "succeeded",
                "inputAudioSha256": arguments.input_audio_sha256,
                "candidates": [cpu_summary, cuda_summary],
                "agreement": agreement,
                "artifact": file_descriptor(arguments.output_artifact),
                "transcriptTextIncludedInSummary": False,
            }
        )
    )
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)

    build = subparsers.add_parser("build-control")
    build.add_argument("--config", required=True, type=Path)
    build.add_argument("--clip-directory", required=True, type=Path)
    build.add_argument("--output-audio", required=True, type=Path)
    build.add_argument("--output-manifest", required=True, type=Path)
    build.set_defaults(handler=build_control)

    control = subparsers.add_parser("run-control")
    control.add_argument("--candidate-id", required=True)
    control.add_argument("--device", required=True, choices=("cpu", "cuda"))
    control.add_argument("--compute-type", required=True)
    control.add_argument("--model-path", required=True, type=Path)
    control.add_argument("--config", required=True, type=Path)
    control.add_argument("--manifest", required=True, type=Path)
    control.add_argument("--control-audio", required=True, type=Path)
    control.add_argument("--clip-directory", required=True, type=Path)
    control.add_argument("--output-artifact", required=True, type=Path)
    control.set_defaults(handler=run_control)

    fixture = subparsers.add_parser("analyze-fixture")
    fixture.add_argument("--cpu-artifact", required=True, type=Path)
    fixture.add_argument("--cpu-artifact-sha256", required=True)
    fixture.add_argument("--cuda-artifact", required=True, type=Path)
    fixture.add_argument("--cuda-artifact-sha256", required=True)
    fixture.add_argument("--input-audio-sha256", required=True)
    fixture.add_argument("--output-artifact", required=True, type=Path)
    fixture.set_defaults(handler=analyze_fixture)
    return parser


def main() -> int:
    arguments = build_parser().parse_args()
    return int(arguments.handler(arguments))


if __name__ == "__main__":
    sys.exit(main())
