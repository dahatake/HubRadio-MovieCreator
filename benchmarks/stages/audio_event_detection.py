"""Compares local audio-event and recording-quality candidates on the Stage 0 fixture.

The benchmark deliberately separates objective decode evidence, deterministic waveform rules,
Silero speech/non-speech segmentation, and YAMNet audio-event classification. All thresholds are
candidate Stage 0 configurations rather than production acceptance values. No candidate is applied
as an edit, and no precision or recall is reported without independent removal annotations.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import shutil
import statistics
import sys
import time
import urllib.request
import uuid
import wave
from collections import Counter
from importlib import metadata
from pathlib import Path

import numpy as np
import av
from faster_whisper.audio import decode_audio
from faster_whisper.vad import VadOptions, get_speech_timestamps
from mediapipe.tasks.python.audio import audio_classifier
from mediapipe.tasks.python.components.containers import audio_data


YAMNET_MODEL_URL = (
    "https://storage.googleapis.com/mediapipe-models/audio_classifier/"
    "yamnet/float32/1/yamnet.tflite"
)
YAMNET_MODEL_BYTE_SIZE = 4_126_810
YAMNET_MODEL_SHA256 = "4d8b4a53282dc83ef04e3e7dbc4fbc98082e34e44ed798e16c3a0cdd4c584faf"
SPEECH_CONTROL_URL = "https://storage.googleapis.com/mediapipe-assets/speech_16000_hz_mono.wav"
SPEECH_CONTROL_BYTE_SIZE = 136_798
SPEECH_CONTROL_SHA256 = "71caf50b8757d6ab9cad5eae4d36669d3c20c225a51660afd7fe0dc44cdb74f6"
SILERO_MODEL_BYTE_SIZE = 1_245_151
SILERO_MODEL_SHA256 = "4cbf549b8326f60f80f2536d9eefeb450a9abe83365a098031c89719f1be17d2"

ANALYSIS_SAMPLE_RATE = 16_000
WAVEFORM_WINDOW_MILLISECONDS = 100
LOW_ENERGY_DBFS = -50.0
LOW_ENERGY_MINIMUM_SECONDS = 2.0
CLIPPING_AMPLITUDE = 0.999
CLIPPING_MINIMUM_SAMPLES_PER_WINDOW = 3
IMBALANCE_ACTIVE_FLOOR_DBFS = -40.0
IMBALANCE_DIFFERENCE_DB = 12.0
IMBALANCE_MINIMUM_SECONDS = 2.0
TRANSIENT_PEAK_AMPLITUDE = 0.98
TRANSIENT_MAXIMUM_RMS_DBFS = -20.0
YAMNET_SCORE_THRESHOLD = 0.3
VAD_THRESHOLD = 0.5
VAD_MINIMUM_SPEECH_MILLISECONDS = 250
VAD_MINIMUM_SILENCE_MILLISECONDS = 2_000

YAMNET_EVENT_LABELS = {
    "cough": {"Cough"},
    "noise": {
        "Noise",
        "Environmental noise",
        "White noise",
        "Pink noise",
        "Static",
        "Hiss",
        "Hum",
        "Buzz",
    },
    "wait": {"Silence"},
}


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def describe_and_verify_asset(
    path: Path,
    source_url: str,
    downloaded: bool,
    expected_byte_size: int,
    expected_sha256: str,
    label: str,
) -> dict:
    byte_size = path.stat().st_size
    digest = file_sha256(path)
    if byte_size != expected_byte_size or digest != expected_sha256:
        raise ValueError(
            f"{label} does not match the pinned byte size and SHA-256 "
            f"(bytes={byte_size}, sha256={digest})."
        )
    return {
        "fileName": path.name,
        "byteSize": byte_size,
        "sha256": digest,
        "downloadedDuringThisRun": downloaded,
        "sourceUrl": source_url,
    }


def ensure_asset(
    path: Path,
    source_url: str,
    expected_byte_size: int,
    expected_sha256: str,
    label: str,
) -> dict:
    downloaded = False
    if path.exists():
        return describe_and_verify_asset(
            path,
            source_url,
            downloaded,
            expected_byte_size,
            expected_sha256,
            label,
        )

    path.parent.mkdir(parents=True, exist_ok=True)
    staging_path = path.with_name(f".{path.name}.{uuid.uuid4().hex}.tmp")
    try:
        with urllib.request.urlopen(source_url, timeout=120) as response:  # noqa: S310 - pinned HTTPS asset
            with staging_path.open("xb") as output:
                shutil.copyfileobj(response, output)
        describe_and_verify_asset(
            staging_path,
            source_url,
            downloaded=True,
            expected_byte_size=expected_byte_size,
            expected_sha256=expected_sha256,
            label=label,
        )
        os.replace(staging_path, path)
        downloaded = True
    finally:
        staging_path.unlink(missing_ok=True)

    return describe_and_verify_asset(
        path,
        source_url,
        downloaded,
        expected_byte_size,
        expected_sha256,
        label,
    )


def read_mono_pcm16_wave(path: Path) -> tuple[int, np.ndarray]:
    with wave.open(str(path), "rb") as handle:
        if handle.getsampwidth() != 2 or handle.getnchannels() != 1:
            raise ValueError("The speech positive-control WAV must be 16-bit mono PCM.")
        sample_rate = handle.getframerate()
        samples = np.frombuffer(handle.readframes(handle.getnframes()), dtype="<i2")
    return sample_rate, samples.astype(np.float32) / 32768.0


def inspect_decoded_audio_timing(path: Path) -> dict:
    with av.open(str(path), mode="r", metadata_errors="ignore") as container:
        if not container.streams.audio:
            raise ValueError("The source does not contain an audio stream.")
        stream = container.streams.audio[0]
        first_frame = next(container.decode(audio=0), None)
        if first_frame is None or first_frame.pts is None or first_frame.time is None:
            raise ValueError("The first decoded audio frame does not carry a presentation timestamp.")
        return {
            "streamTimeBase": str(stream.time_base),
            "streamStartTime": stream.start_time,
            "firstDecodedFramePts": int(first_frame.pts),
            "firstDecodedFrameSeconds": float(first_frame.time),
            "firstDecodedFrameSampleCount": int(first_frame.samples),
            "firstDecodedFrameSampleRate": int(first_frame.sample_rate),
        }


def dbfs_from_power(power: np.ndarray | float) -> np.ndarray | float:
    values = np.asarray(power, dtype=np.float64)
    result = np.full(values.shape, -200.0, dtype=np.float64)
    positive = values > np.finfo(np.float64).tiny
    result[positive] = 10.0 * np.log10(values[positive])
    if result.ndim == 0:
        return float(result)
    return result


def metric_summary(values: np.ndarray) -> dict:
    finite = values[np.isfinite(values)]
    if finite.size == 0:
        return {"minimum": None, "median": None, "maximum": None}
    return {
        "minimum": round(float(np.min(finite)), 6),
        "median": round(float(np.median(finite)), 6),
        "maximum": round(float(np.max(finite)), 6),
    }


def merge_flagged_windows(
    flags: np.ndarray,
    metrics: dict[str, tuple[np.ndarray, str]],
    sample_count: int,
    sample_rate: int,
    window_samples: int,
    source_start_seconds: float,
    minimum_duration_seconds: float,
) -> list[dict]:
    candidates: list[dict] = []
    start_window: int | None = None

    def append_candidate(first: int, last_exclusive: int) -> None:
        start_sample = first * window_samples
        end_sample = min(last_exclusive * window_samples, sample_count)
        duration = (end_sample - start_sample) / sample_rate
        if duration + 1e-9 < minimum_duration_seconds:
            return
        candidate: dict[str, object] = {
            "sourceStartSeconds": round(source_start_seconds + start_sample / sample_rate, 6),
            "sourceEndSeconds": round(source_start_seconds + end_sample / sample_rate, 6),
            "durationSeconds": round(duration, 6),
        }
        for output_name, (series, reduction) in metrics.items():
            selected = series[first:last_exclusive]
            if selected.size == 0:
                candidate[output_name] = None
            elif reduction == "min":
                candidate[output_name] = round(float(np.min(selected)), 6)
            elif reduction == "mean":
                candidate[output_name] = round(float(np.mean(selected)), 6)
            else:
                candidate[output_name] = round(float(np.max(selected)), 6)
        candidates.append(candidate)

    for index, flagged in enumerate(flags.tolist()):
        if flagged and start_window is None:
            start_window = index
        elif not flagged and start_window is not None:
            append_candidate(start_window, index)
            start_window = None
    if start_window is not None:
        append_candidate(start_window, len(flags))
    return candidates


def waveform_analysis(
    stereo: np.ndarray,
    sample_rate: int,
    source_start_seconds: float,
) -> dict:
    if stereo.ndim != 2 or stereo.shape[1] != 2:
        raise ValueError("Waveform analysis requires a two-channel array.")
    if not np.isfinite(stereo).all():
        raise ValueError("Decoded audio contains non-finite samples.")

    started = time.perf_counter()
    mono = np.mean(stereo, axis=1, dtype=np.float32)
    window_samples = sample_rate * WAVEFORM_WINDOW_MILLISECONDS // 1000
    window_count = math.ceil(len(mono) / window_samples)
    padded_count = window_count * window_samples
    valid_counts = np.full(window_count, window_samples, dtype=np.int64)
    valid_counts[-1] = len(mono) - (window_count - 1) * window_samples

    mono_padded = np.pad(mono, (0, padded_count - len(mono))).reshape(window_count, window_samples)
    stereo_padded = np.pad(stereo, ((0, padded_count - len(stereo)), (0, 0))).reshape(
        window_count, window_samples, 2
    )

    mono_power = np.sum(np.square(mono_padded, dtype=np.float64), axis=1) / valid_counts
    channel_power = np.sum(np.square(stereo_padded, dtype=np.float64), axis=1) / valid_counts[:, None]
    mono_rms_dbfs = np.asarray(dbfs_from_power(mono_power))
    channel_rms_dbfs = np.asarray(dbfs_from_power(channel_power))
    peak = np.max(np.abs(stereo_padded), axis=(1, 2))
    near_full_scale_counts = np.sum(np.abs(stereo_padded) >= CLIPPING_AMPLITUDE, axis=(1, 2))
    near_full_scale_share = near_full_scale_counts / (valid_counts * 2)
    channel_difference_db = np.abs(channel_rms_dbfs[:, 0] - channel_rms_dbfs[:, 1])
    active_channel_dbfs = np.max(channel_rms_dbfs, axis=1)

    low_energy_flags = mono_rms_dbfs <= LOW_ENERGY_DBFS
    clipping_flags = near_full_scale_counts >= CLIPPING_MINIMUM_SAMPLES_PER_WINDOW
    imbalance_flags = (
        (active_channel_dbfs > IMBALANCE_ACTIVE_FLOOR_DBFS)
        & (channel_difference_db >= IMBALANCE_DIFFERENCE_DB)
    )
    transient_flags = (
        (peak >= TRANSIENT_PEAK_AMPLITUDE)
        & (mono_rms_dbfs <= TRANSIENT_MAXIMUM_RMS_DBFS)
        & ~clipping_flags
    )

    candidates = {
        "lowEnergyOrWait": merge_flagged_windows(
            low_energy_flags,
            {"minimumRmsDbfs": (mono_rms_dbfs, "min")},
            len(mono),
            sample_rate,
            window_samples,
            source_start_seconds,
            LOW_ENERGY_MINIMUM_SECONDS,
        ),
        "potentialClipping": merge_flagged_windows(
            clipping_flags,
            {"maximumNearFullScaleShare": (near_full_scale_share, "max")},
            len(mono),
            sample_rate,
            window_samples,
            source_start_seconds,
            WAVEFORM_WINDOW_MILLISECONDS / 1000,
        ),
        "channelImbalance": merge_flagged_windows(
            imbalance_flags,
            {"maximumChannelDifferenceDb": (channel_difference_db, "max")},
            len(mono),
            sample_rate,
            window_samples,
            source_start_seconds,
            IMBALANCE_MINIMUM_SECONDS,
        ),
        "highCrestTransient": merge_flagged_windows(
            transient_flags,
            {
                "maximumPeak": (peak, "max"),
                "minimumRmsDbfs": (mono_rms_dbfs, "min"),
            },
            len(mono),
            sample_rate,
            window_samples,
            source_start_seconds,
            WAVEFORM_WINDOW_MILLISECONDS / 1000,
        ),
    }

    channel_global_power = np.mean(np.square(stereo, dtype=np.float64), axis=0)
    channel_global_dbfs = np.asarray(dbfs_from_power(channel_global_power))
    global_summary = {
        "decodedDurationSeconds": round(len(mono) / sample_rate, 6),
        "leftRmsDbfs": round(float(channel_global_dbfs[0]), 6),
        "rightRmsDbfs": round(float(channel_global_dbfs[1]), 6),
        "absoluteChannelDifferenceDb": round(float(abs(channel_global_dbfs[0] - channel_global_dbfs[1])), 6),
        "peakAmplitude": round(float(np.max(np.abs(stereo))), 6),
        "nearFullScaleSampleCount": int(np.sum(np.abs(stereo) >= CLIPPING_AMPLITUDE)),
        "windowRmsDbfs": metric_summary(mono_rms_dbfs),
    }

    return {
        "configuration": {
            "analysisSampleRate": sample_rate,
            "windowMilliseconds": WAVEFORM_WINDOW_MILLISECONDS,
            "lowEnergyDbfs": LOW_ENERGY_DBFS,
            "lowEnergyMinimumSeconds": LOW_ENERGY_MINIMUM_SECONDS,
            "clippingAmplitude": CLIPPING_AMPLITUDE,
            "clippingMinimumSamplesPerWindow": CLIPPING_MINIMUM_SAMPLES_PER_WINDOW,
            "imbalanceActiveFloorDbfs": IMBALANCE_ACTIVE_FLOOR_DBFS,
            "imbalanceDifferenceDb": IMBALANCE_DIFFERENCE_DB,
            "imbalanceMinimumSeconds": IMBALANCE_MINIMUM_SECONDS,
            "transientPeakAmplitude": TRANSIENT_PEAK_AMPLITUDE,
            "transientMaximumRmsDbfs": TRANSIENT_MAXIMUM_RMS_DBFS,
        },
        "global": global_summary,
        "candidateCounts": {name: len(items) for name, items in candidates.items()},
        "candidates": candidates,
        "elapsedSeconds": round(time.perf_counter() - started, 3),
    }


def build_waveform_control(sample_rate: int) -> np.ndarray:
    total_seconds = 9
    times = np.arange(total_seconds * sample_rate, dtype=np.float64) / sample_rate
    control = np.zeros((len(times), 2), dtype=np.float32)
    clean = times < 2
    control[clean, 0] = (0.2 * np.sin(2 * np.pi * 440 * times[clean])).astype(np.float32)
    control[clean, 1] = control[clean, 0]
    clipped = (times >= 5) & (times < 6)
    control[clipped, 0] = np.where(np.sin(2 * np.pi * 440 * times[clipped]) >= 0, 1.0, -1.0)
    control[clipped, 1] = control[clipped, 0]
    imbalanced = times >= 6
    control[imbalanced, 0] = (0.2 * np.sin(2 * np.pi * 440 * times[imbalanced])).astype(np.float32)
    control[imbalanced, 1] = 0.0
    return control


def complement_speech_spans(
    speech_spans: list[dict],
    sample_count: int,
    sample_rate: int,
    source_start_seconds: float,
    minimum_seconds: float,
) -> list[dict]:
    candidates: list[dict] = []
    previous_end = 0
    for speech in speech_spans:
        start = int(speech["start"])
        if start > previous_end and (start - previous_end) / sample_rate >= minimum_seconds:
            candidates.append(
                {
                    "sourceStartSeconds": round(source_start_seconds + previous_end / sample_rate, 6),
                    "sourceEndSeconds": round(source_start_seconds + start / sample_rate, 6),
                    "durationSeconds": round((start - previous_end) / sample_rate, 6),
                }
            )
        previous_end = max(previous_end, int(speech["end"]))
    if sample_count > previous_end and (sample_count - previous_end) / sample_rate >= minimum_seconds:
        candidates.append(
            {
                "sourceStartSeconds": round(source_start_seconds + previous_end / sample_rate, 6),
                "sourceEndSeconds": round(source_start_seconds + sample_count / sample_rate, 6),
                "durationSeconds": round((sample_count - previous_end) / sample_rate, 6),
            }
        )
    return candidates


def run_vad(
    mono: np.ndarray,
    sample_rate: int,
    source_start_seconds: float,
) -> dict:
    started = time.perf_counter()
    options = VadOptions(
        threshold=VAD_THRESHOLD,
        min_speech_duration_ms=VAD_MINIMUM_SPEECH_MILLISECONDS,
        min_silence_duration_ms=VAD_MINIMUM_SILENCE_MILLISECONDS,
        speech_pad_ms=0,
    )
    speech_spans = get_speech_timestamps(mono, options, sampling_rate=sample_rate)
    wait_candidates = complement_speech_spans(
        speech_spans,
        len(mono),
        sample_rate,
        source_start_seconds,
        VAD_MINIMUM_SILENCE_MILLISECONDS / 1000,
    )
    speech_seconds = sum((int(span["end"]) - int(span["start"])) / sample_rate for span in speech_spans)
    return {
        "configuration": {
            "threshold": VAD_THRESHOLD,
            "minimumSpeechMilliseconds": VAD_MINIMUM_SPEECH_MILLISECONDS,
            "minimumSilenceMilliseconds": VAD_MINIMUM_SILENCE_MILLISECONDS,
            "speechPadMilliseconds": 0,
        },
        "speechSpanCount": len(speech_spans),
        "speechDurationSeconds": round(speech_seconds, 6),
        "waitCandidateCount": len(wait_candidates),
        "waitCandidates": wait_candidates,
        "elapsedSeconds": round(time.perf_counter() - started, 3),
    }


def categories_by_name(result) -> dict[str, float]:
    if not result.classifications:
        return {}
    return {
        category.category_name: float(category.score)
        for category in result.classifications[0].categories
        if category.category_name
    }


def control_label_maxima(results, labels: set[str]) -> dict[str, float]:
    maxima = {label: 0.0 for label in sorted(labels)}
    for result in results:
        scores = categories_by_name(result)
        for label in maxima:
            maxima[label] = max(maxima[label], scores.get(label, 0.0))
    return {label: round(score, 6) for label, score in maxima.items()}


def merge_classification_windows(
    windows: list[dict],
    source_start_seconds: float,
    decoded_duration_seconds: float,
    nominal_step_seconds: float,
) -> dict[str, list[dict]]:
    merged: dict[str, list[dict]] = {event: [] for event in YAMNET_EVENT_LABELS}
    for event in YAMNET_EVENT_LABELS:
        active = [window for window in windows if window["events"].get(event)]
        for window in active:
            event_value = window["events"][event]
            start = source_start_seconds + window["timestampSeconds"]
            end = source_start_seconds + min(
                decoded_duration_seconds,
                window["timestampSeconds"] + nominal_step_seconds,
            )
            if end <= start:
                continue
            if merged[event] and start <= merged[event][-1]["sourceEndSeconds"] + 0.05:
                current = merged[event][-1]
                current["sourceEndSeconds"] = round(max(current["sourceEndSeconds"], end), 6)
                current["durationSeconds"] = round(current["sourceEndSeconds"] - current["sourceStartSeconds"], 6)
                current["windowCount"] += 1
                current["maximumScore"] = round(max(current["maximumScore"], event_value["score"]), 6)
                current["scoreSum"] += event_value["score"]
                current["labels"].add(event_value["label"])
            else:
                merged[event].append(
                    {
                        "sourceStartSeconds": round(start, 6),
                        "sourceEndSeconds": round(end, 6),
                        "durationSeconds": round(end - start, 6),
                        "windowCount": 1,
                        "maximumScore": round(event_value["score"], 6),
                        "scoreSum": event_value["score"],
                        "labels": {event_value["label"]},
                    }
                )

    for candidates in merged.values():
        for candidate in candidates:
            candidate["meanScore"] = round(candidate.pop("scoreSum") / candidate["windowCount"], 6)
            candidate["labels"] = sorted(candidate["labels"])
    return merged


def run_yamnet(
    classifier: audio_classifier.AudioClassifier,
    mono: np.ndarray,
    sample_rate: int,
    source_start_seconds: float,
) -> dict:
    started = time.perf_counter()
    results = classifier.classify(audio_data.AudioData.create_from_array(mono, sample_rate))
    timestamps = [int(result.timestamp_ms) for result in results]
    if any(right <= left for left, right in zip(timestamps, timestamps[1:])):
        raise ValueError("YAMNet result timestamps are not strictly increasing.")
    steps = [(right - left) / 1000 for left, right in zip(timestamps, timestamps[1:])]
    nominal_step = statistics.median(steps) if steps else 0.975

    top_categories: Counter[str] = Counter()
    windows: list[dict] = []
    for result in results:
        scores = categories_by_name(result)
        if scores:
            top_categories[max(scores, key=lambda label: scores[label])] += 1
        events: dict[str, dict] = {}
        for event, labels in YAMNET_EVENT_LABELS.items():
            best_label = max(labels, key=lambda label: scores.get(label, 0.0))
            best_score = scores.get(best_label, 0.0)
            if best_score >= YAMNET_SCORE_THRESHOLD:
                events[event] = {"label": best_label, "score": best_score}
        windows.append({"timestampSeconds": result.timestamp_ms / 1000, "events": events})

    candidates = merge_classification_windows(
        windows,
        source_start_seconds,
        len(mono) / sample_rate,
        nominal_step,
    )
    return {
        "configuration": {
            "scoreThreshold": YAMNET_SCORE_THRESHOLD,
            "mappedLabels": {event: sorted(labels) for event, labels in YAMNET_EVENT_LABELS.items()},
            "thresholdIsProductionApproved": False,
        },
        "classificationResultCount": len(results),
        "nominalStepSeconds": round(nominal_step, 6),
        "firstTimestampMilliseconds": timestamps[0] if timestamps else None,
        "lastTimestampMilliseconds": timestamps[-1] if timestamps else None,
        "topCategoryDistribution": [
            {"category": category, "count": count}
            for category, count in top_categories.most_common(20)
        ],
        "candidateCounts": {event: len(items) for event, items in candidates.items()},
        "candidates": candidates,
        "elapsedSeconds": round(time.perf_counter() - started, 3),
    }


def interval_intersection_seconds(first: list[dict], second: list[dict]) -> float:
    left = sorted(first, key=lambda item: item["sourceStartSeconds"])
    right = sorted(second, key=lambda item: item["sourceStartSeconds"])
    first_index = 0
    second_index = 0
    total = 0.0
    while first_index < len(left) and second_index < len(right):
        start = max(left[first_index]["sourceStartSeconds"], right[second_index]["sourceStartSeconds"])
        end = min(left[first_index]["sourceEndSeconds"], right[second_index]["sourceEndSeconds"])
        if end > start:
            total += end - start
        if left[first_index]["sourceEndSeconds"] <= right[second_index]["sourceEndSeconds"]:
            first_index += 1
        else:
            second_index += 1
    return total


def total_interval_seconds(items: list[dict]) -> float:
    return sum(max(0.0, item["sourceEndSeconds"] - item["sourceStartSeconds"]) for item in items)


def pairwise_agreement(methods: dict[str, list[dict]]) -> list[dict]:
    names = list(methods)
    comparisons: list[dict] = []
    for first_index, first_name in enumerate(names):
        for second_name in names[first_index + 1 :]:
            first = methods[first_name]
            second = methods[second_name]
            intersection = interval_intersection_seconds(first, second)
            union = total_interval_seconds(first) + total_interval_seconds(second) - intersection
            comparisons.append(
                {
                    "first": first_name,
                    "second": second_name,
                    "intersectionSeconds": round(intersection, 6),
                    "temporalJaccard": None if union <= 0 else round(intersection / union, 6),
                }
            )
    return comparisons


def load_ffmpeg_silence(path: Path) -> dict:
    with path.open("r", encoding="utf-8") as handle:
        report = json.load(handle)
    if not report.get("control", {}).get("passed"):
        raise RuntimeError("FFmpeg silencedetect positive control did not pass.")
    return report


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--source-start-seconds", required=True, type=float)
    parser.add_argument("--yamnet-model-path", required=True, type=Path)
    parser.add_argument("--speech-control-path", required=True, type=Path)
    parser.add_argument("--silero-model-path", required=True, type=Path)
    parser.add_argument("--ffmpeg-silence-json", required=True, type=Path)
    arguments = parser.parse_args()

    model = ensure_asset(
        arguments.yamnet_model_path,
        YAMNET_MODEL_URL,
        YAMNET_MODEL_BYTE_SIZE,
        YAMNET_MODEL_SHA256,
        "YAMNet audio classifier model",
    )
    speech_control_asset = ensure_asset(
        arguments.speech_control_path,
        SPEECH_CONTROL_URL,
        SPEECH_CONTROL_BYTE_SIZE,
        SPEECH_CONTROL_SHA256,
        "MediaPipe speech positive-control WAV",
    )
    silero_model = describe_and_verify_asset(
        arguments.silero_model_path,
        "faster-whisper package asset",
        downloaded=False,
        expected_byte_size=SILERO_MODEL_BYTE_SIZE,
        expected_sha256=SILERO_MODEL_SHA256,
        label="Silero VAD model",
    )
    ffmpeg_silence = load_ffmpeg_silence(arguments.ffmpeg_silence_json)
    decoded_timing = inspect_decoded_audio_timing(arguments.source)
    decoded_timeline_start_seconds = decoded_timing["firstDecodedFrameSeconds"]
    if abs(decoded_timeline_start_seconds - arguments.source_start_seconds) > 0.001:
        raise ValueError(
            "The observed first decoded audio frame time does not match the expected source timeline start "
            f"({decoded_timeline_start_seconds} != {arguments.source_start_seconds})."
        )

    control_sample_rate, speech_control = read_mono_pcm16_wave(arguments.speech_control_path)
    if control_sample_rate != ANALYSIS_SAMPLE_RATE:
        raise ValueError("The pinned speech positive control is not 16 kHz.")

    decode_started = time.perf_counter()
    left, right = decode_audio(str(arguments.source), sampling_rate=ANALYSIS_SAMPLE_RATE, split_stereo=True)
    decode_seconds = time.perf_counter() - decode_started
    if len(left) == 0 or len(left) != len(right):
        raise ValueError("Decoded stereo channel lengths are empty or unequal.")
    stereo = np.column_stack((left, right)).astype(np.float32, copy=False)
    mono = np.mean(stereo, axis=1, dtype=np.float32)

    waveform_control = waveform_analysis(build_waveform_control(ANALYSIS_SAMPLE_RATE), ANALYSIS_SAMPLE_RATE, 0.0)
    waveform_control_passed = all(
        waveform_control["candidateCounts"][name] >= 1
        for name in ("lowEnergyOrWait", "potentialClipping", "channelImbalance")
    )
    if not waveform_control_passed:
        raise RuntimeError("Deterministic waveform positive control did not trigger every required rule.")
    waveform = waveform_analysis(stereo, ANALYSIS_SAMPLE_RATE, decoded_timeline_start_seconds)

    vad_options = VadOptions(
        threshold=VAD_THRESHOLD,
        min_speech_duration_ms=VAD_MINIMUM_SPEECH_MILLISECONDS,
        min_silence_duration_ms=VAD_MINIMUM_SILENCE_MILLISECONDS,
        speech_pad_ms=0,
    )
    vad_speech_control = get_speech_timestamps(speech_control, vad_options, sampling_rate=ANALYSIS_SAMPLE_RATE)
    vad_silence_control = get_speech_timestamps(
        np.zeros(4 * ANALYSIS_SAMPLE_RATE, dtype=np.float32),
        vad_options,
        sampling_rate=ANALYSIS_SAMPLE_RATE,
    )
    vad_control_passed = bool(vad_speech_control) and not vad_silence_control
    if not vad_control_passed:
        raise RuntimeError("Silero VAD speech/silence controls did not produce the expected result.")
    vad = run_vad(mono, ANALYSIS_SAMPLE_RATE, decoded_timeline_start_seconds)

    rng = np.random.default_rng(20260827)
    silence_control = np.zeros(4 * ANALYSIS_SAMPLE_RATE, dtype=np.float32)
    noise_control = rng.normal(0.0, 0.1, 4 * ANALYSIS_SAMPLE_RATE).astype(np.float32)
    with audio_classifier.AudioClassifier.create_from_model_path(str(arguments.yamnet_model_path)) as classifier:
        speech_results = classifier.classify(
            audio_data.AudioData.create_from_array(speech_control, ANALYSIS_SAMPLE_RATE)
        )
        silence_results = classifier.classify(
            audio_data.AudioData.create_from_array(silence_control, ANALYSIS_SAMPLE_RATE)
        )
        noise_results = classifier.classify(
            audio_data.AudioData.create_from_array(noise_control, ANALYSIS_SAMPLE_RATE)
        )
        speech_scores = control_label_maxima(speech_results, {"Speech"})
        silence_scores = control_label_maxima(silence_results, {"Silence"})
        noise_scores = control_label_maxima(noise_results, YAMNET_EVENT_LABELS["noise"])
        yamnet_control_passed = (
            speech_scores["Speech"] >= 0.8
            and silence_scores["Silence"] >= 0.7
            and max(noise_scores.values()) >= YAMNET_SCORE_THRESHOLD
        )
        if not yamnet_control_passed:
            raise RuntimeError("YAMNet speech/silence/noise controls did not produce the expected result.")
        yamnet = run_yamnet(classifier, mono, ANALYSIS_SAMPLE_RATE, decoded_timeline_start_seconds)

    wait_methods = {
        "ffmpegSilencedetect": ffmpeg_silence["fixture"]["candidates"],
        "waveformLowEnergy": waveform["candidates"]["lowEnergyOrWait"],
        "sileroVad": vad["waitCandidates"],
        "yamnetSilence": yamnet["candidates"]["wait"],
    }
    noise_methods = {
        "waveformHighCrestTransient": waveform["candidates"]["highCrestTransient"],
        "yamnetNoise": yamnet["candidates"]["noise"],
    }

    report = {
        "packages": {
            "mediapipe": metadata.version("mediapipe"),
            "fasterWhisper": metadata.version("faster-whisper"),
            "onnxruntime": metadata.version("onnxruntime"),
            "av": metadata.version("av"),
            "numpy": metadata.version("numpy"),
        },
        "models": {
            "yamnet": model,
            "sileroVad": silero_model,
        },
        "analysisInput": {
            "sampleRate": ANALYSIS_SAMPLE_RATE,
            "channels": 2,
            "decodedSampleCountPerChannel": len(left),
            "decodedDurationSeconds": round(len(left) / ANALYSIS_SAMPLE_RATE, 6),
            "sourceTimelineStartSeconds": decoded_timeline_start_seconds,
            "decodedTimelineEvidence": decoded_timing,
            "sourceAudioPersisted": False,
        },
        "controls": {
            "ffmpegSilencedetect": ffmpeg_silence["control"],
            "waveformRules": {
                "passed": waveform_control_passed,
                "candidateCounts": waveform_control["candidateCounts"],
                "expectedConditions": ["lowEnergyOrWait", "potentialClipping", "channelImbalance"],
            },
            "sileroVad": {
                "passed": vad_control_passed,
                "speechControlAsset": speech_control_asset,
                "speechSpanCountOnSpeechControl": len(vad_speech_control),
                "speechSpanCountOnSilenceControl": len(vad_silence_control),
            },
            "yamnet": {
                "passed": yamnet_control_passed,
                "speechControlAsset": speech_control_asset,
                "speechScores": speech_scores,
                "silenceScores": silence_scores,
                "noiseScores": noise_scores,
                "coughPositiveControlAvailable": False,
            },
        },
        "methods": {
            "ffmpegSilencedetect": {
                "configuration": ffmpeg_silence["configuration"],
                **ffmpeg_silence["fixture"],
            },
            "waveformRules": waveform,
            "sileroVad": vad,
            "yamnet": yamnet,
        },
        "agreement": {
            "waitCandidateComparisons": pairwise_agreement(wait_methods),
            "noiseCandidateComparisons": pairwise_agreement(noise_methods),
            "interpretationRequiresAnnotations": True,
        },
        "performance": {
            "decodeSeconds": round(decode_seconds, 3),
            "waveformSeconds": waveform["elapsedSeconds"],
            "sileroVadSeconds": vad["elapsedSeconds"],
            "yamnetSeconds": yamnet["elapsedSeconds"],
        },
        "privacy": {
            "sourceAudioPersisted": False,
            "sourceAudioSamplesIncludedInReport": False,
            "sourceTranscriptIncludedInReport": False,
            "candidateSourceTimesIncludedInRawReport": True,
        },
        "annotations": {
            "removalReferenceAvailable": False,
            "precisionRecallMeasured": False,
        },
        "automaticDeletionPerformed": False,
        "candidateThresholdsAreStage0ConfigurationsNotProductionValues": True,
    }

    json.dump(report, sys.stdout, ensure_ascii=False)
    return 0


if __name__ == "__main__":
    sys.exit(main())
