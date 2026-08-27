"""Validate ST-012 manifests and analyze rendered media without publishing source pixels or audio."""

from __future__ import annotations

import argparse
import bisect
import hashlib
import json
import math
import os
import re
import statistics
import subprocess
import sys
import uuid
from fractions import Fraction
from pathlib import Path
from typing import Any

import cv2
import numpy as np


VOLUME_PATTERN = re.compile(r"\[(?:Parsed_)?volumedetect[^]]*\]\s+(mean_volume|max_volume):\s+(-?inf|[-+0-9.]+)\s+dB")


def read_json_object(path: Path) -> dict[str, Any]:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError(f"Expected a JSON object: {path.name}")
    return value


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def file_descriptor(path: Path) -> dict[str, Any]:
    return {
        "fileName": path.name,
        "byteSize": path.stat().st_size,
        "sha256": file_sha256(path),
    }


def rational_seconds(value: dict[str, Any]) -> Fraction:
    numerator = int(value["value"])
    denominator = int(value["rate"])
    if denominator <= 0:
        raise ValueError("Rational rate must be positive.")
    return Fraction(numerator, denominator)


def validate_manifest(arguments: argparse.Namespace) -> int:
    manifest = read_json_object(arguments.manifest)
    if manifest.get("schemaVersion") != "1.0.0" or manifest.get("manifestId") != "st-012-manual-render-v1":
        raise ValueError("Unexpected ST-012 manifest identity.")
    source = manifest["source"]
    if (
        source["sha256"] != arguments.source_sha256
        or int(source["byteSize"]) != int(arguments.source_byte_size)
        or int(source["width"]) != 3840
        or int(source["height"]) != 2160
        or source["timingAuthority"] != "observed-source-presentation-timestamps"
    ):
        raise ValueError("ST-012 manifest source does not match the validated fixture evidence.")
    rights = manifest["rights"]
    if (
        rights["status"] != "unconfirmed"
        or rights["allowedUse"] != "local-technical-evaluation-only"
        or rights["externalPublicationAllowed"] is not False
    ):
        raise ValueError("ST-012 manifest does not preserve the fixture rights boundary.")
    toolchain = manifest["toolchain"]
    if (
        toolchain["ffmpegSha256"] != arguments.ffmpeg_sha256
        or toolchain["ffprobeSha256"] != arguments.ffprobe_sha256
        or toolchain["videoEncoder"] != "h264_nvenc"
        or toolchain["productionApproved"] is not False
    ):
        raise ValueError("ST-012 manifest toolchain differs from the approved Stage 0 inputs.")
    subtitle_path = arguments.manifest.parent / str(manifest["subtitle"]["relativePath"])
    if (
        manifest["subtitle"]["relativePath"] != "manual-render-subtitles.ass"
        or Path(str(manifest["subtitle"]["relativePath"])).name
        != str(manifest["subtitle"]["relativePath"])
        or not subtitle_path.is_file()
        or file_sha256(subtitle_path) != manifest["subtitle"]["sha256"]
        or manifest["subtitle"]["sourceTranscriptDerived"] is not False
    ):
        raise ValueError("ST-012 neutral subtitle fixture is missing or changed.")
    profiles = {str(item["profileId"]): item for item in manifest["renderProfiles"]}
    if set(profiles) != {"landscape-1280x720", "vertical-1080x1920"}:
        raise ValueError("ST-012 requires exactly the landscape and vertical profiles.")
    if (
        (int(profiles["landscape-1280x720"]["width"]), int(profiles["landscape-1280x720"]["height"]))
        != (1280, 720)
        or (
            int(profiles["vertical-1080x1920"]["width"]),
            int(profiles["vertical-1080x1920"]["height"]),
        )
        != (1080, 1920)
    ):
        raise ValueError("ST-012 render profile dimensions changed.")
    compositions = {str(item["compositionId"]): item for item in manifest["compositions"]}
    if set(compositions) != {
        "landscape-trim-concat-subtitle-audio",
        "vertical-trim-center-crop",
    }:
        raise ValueError("ST-012 requires exactly two compositions.")
    required_operations = {"trim", "concat", "crop", "subtitle", "audio-gain"}
    observed_operations = {
        str(operation)
        for composition in compositions.values()
        for operation in composition["operations"]
    }
    if not required_operations.issubset(observed_operations):
        raise ValueError("ST-012 manifest does not exercise every required operation.")

    source_duration = Fraction(str(source["durationSeconds"]))
    segment_count = 0
    reverse_map_sample_count = 0
    for composition in compositions.values():
        profile_id = str(composition["profileId"])
        if profile_id not in profiles:
            raise ValueError("Composition references an unknown profile.")
        previous_output_end = Fraction(0)
        segments = composition["segments"]
        if not isinstance(segments, list) or not segments:
            raise ValueError("Composition has no segments.")
        for index, segment in enumerate(segments):
            source_start = rational_seconds(segment["sourceStart"])
            source_duration_value = rational_seconds(segment["sourceDuration"])
            output_start = rational_seconds(segment["outputStart"])
            output_duration = rational_seconds(segment["outputDuration"])
            if source_start < 0 or source_duration_value <= 0 or source_start + source_duration_value > source_duration:
                raise ValueError("Composition source segment is outside the fixture.")
            if output_start != previous_output_end or output_duration != source_duration_value:
                raise ValueError("Composition output segments must be contiguous one-times-speed mappings.")
            previous_output_end = output_start + output_duration
            segment_count += 1
            if index > 0 and "concat" not in composition["operations"]:
                raise ValueError("A multi-segment composition must declare concat.")
        for sample in composition["reverseMapSamples"]:
            output_time = rational_seconds(sample["requestedOutput"])
            if output_time < 0 or output_time >= previous_output_end:
                raise ValueError("Reverse-map sample is outside the composition output.")
            reverse_map_sample_count += 1

    vertical = compositions["vertical-trim-center-crop"]
    crop = vertical["crop"]
    crop_tuple = (int(crop["x"]), int(crop["y"]), int(crop["width"]), int(crop["height"]))
    if crop_tuple != (1380, 120, 1080, 1920):
        raise ValueError("Vertical crop must be the exact-ratio centered 1080x1920 source region.")
    if crop_tuple[0] + crop_tuple[2] > int(source["width"]) or crop_tuple[1] + crop_tuple[3] > int(source["height"]):
        raise ValueError("Vertical crop exceeds the source frame.")
    if Fraction(crop_tuple[2], crop_tuple[3]) != Fraction(9, 16):
        raise ValueError("Vertical crop is not exactly 9:16.")
    if float(manifest["audioMechanism"]["linearGain"]) != 0.5:
        raise ValueError("ST-012 audio mechanism must retain its declared half-amplitude control.")
    if manifest["assessment"]["perceptualQualityMeasured"] is not False:
        raise ValueError("ST-012 manifest unexpectedly claims perceptual quality measurement.")

    print(
        json.dumps(
            {
                "status": "succeeded",
                "manifestId": manifest["manifestId"],
                "manifestSha256": file_sha256(arguments.manifest),
                "subtitle": file_descriptor(subtitle_path),
                "profileCount": len(profiles),
                "compositionCount": len(compositions),
                "segmentCount": segment_count,
                "reverseMapSampleCount": reverse_map_sample_count,
                "requiredOperations": sorted(required_operations),
                "allRequiredOperationsDeclared": True,
                "verticalCropExactNineBySixteen": True,
                "sourceTextUsedBySubtitle": False,
                "productionThresholdsApproved": False,
            }
        )
    )
    return 0


def load_ticks(path: Path, time_base: Fraction) -> list[float]:
    values: list[int] = []
    with path.open("r", encoding="utf-8") as handle:
        for line in handle:
            token = line.strip().split(",")[0]
            if token and token != "N/A":
                values.append(int(token))
    if not values:
        raise ValueError("PTS evidence is empty.")
    values.sort()
    return [float(Fraction(value) * time_base) for value in values]


def parse_fraction(value: str) -> Fraction:
    return Fraction(value)


def composition_by_id(manifest: dict[str, Any], composition_id: str) -> dict[str, Any]:
    matches = [item for item in manifest["compositions"] if item["compositionId"] == composition_id]
    if len(matches) != 1:
        raise ValueError("Unknown or duplicate composition ID.")
    return matches[0]


def source_filter(composition: dict[str, Any], width: int, height: int) -> str:
    if composition["compositionId"] == "vertical-trim-center-crop":
        crop = composition["crop"]
        return (
            f"crop={int(crop['width'])}:{int(crop['height'])}:"
            f"{int(crop['x'])}:{int(crop['y'])},"
            f"scale={width}:{height}:flags=lanczos,format=gray"
        )
    return f"scale={width}:{height}:flags=lanczos,format=gray"


def extract_gray(
    ffmpeg: Path,
    video: Path,
    seconds: float,
    filter_graph: str,
    expected_bytes: int,
) -> np.ndarray:
    completed = subprocess.run(
        [
            str(ffmpeg),
            "-nostdin",
            "-v",
            "error",
            "-ss",
            f"{seconds:.9f}",
            "-i",
            str(video),
            "-frames:v",
            "1",
            "-vf",
            filter_graph,
            "-f",
            "rawvideo",
            "-pix_fmt",
            "gray",
            "pipe:1",
        ],
        capture_output=True,
        timeout=300,
        check=False,
    )
    if completed.returncode != 0 or len(completed.stdout) != expected_bytes:
        raise RuntimeError("FFmpeg could not extract one expected-size comparison frame.")
    return np.frombuffer(completed.stdout, dtype=np.uint8).copy()


def absolute_difference(left: np.ndarray, right: np.ndarray) -> float:
    if left.shape != right.shape or left.size == 0:
        raise ValueError("Comparison frames do not share a usable shape.")
    return float(np.mean(np.abs(left.astype(np.int16) - right.astype(np.int16))))


def map_output_time(
    composition: dict[str, Any], output_seconds: float
) -> tuple[dict[str, Any], float]:
    for segment in composition["segments"]:
        output_start = float(rational_seconds(segment["outputStart"]))
        output_duration = float(rational_seconds(segment["outputDuration"]))
        if output_start <= output_seconds < output_start + output_duration:
            source_start = float(rational_seconds(segment["sourceStart"]))
            source_duration = float(rational_seconds(segment["sourceDuration"]))
            mapped = source_start + (output_seconds - output_start) * source_duration / output_duration
            return segment, mapped
    raise ValueError("Observed output PTS is outside the manifest timeline.")


def frame_comparison(
    ffmpeg: Path,
    source_video: Path,
    output_video: Path,
    composition: dict[str, Any],
    output_seconds: float,
    source_seconds: float,
    control_seconds: float,
    width: int,
    height: int,
) -> tuple[float, float]:
    expected_bytes = width * height
    output_frame = extract_gray(
        ffmpeg,
        output_video,
        output_seconds,
        f"scale={width}:{height}:flags=lanczos,format=gray",
        expected_bytes,
    )
    transform = source_filter(composition, width, height)
    source_frame = extract_gray(ffmpeg, source_video, source_seconds, transform, expected_bytes)
    control_frame = extract_gray(ffmpeg, source_video, control_seconds, transform, expected_bytes)
    return absolute_difference(output_frame, source_frame), absolute_difference(output_frame, control_frame)


def percentile(values: list[float], probability: float) -> float | None:
    if not values:
        return None
    ordered = sorted(values)
    if len(ordered) == 1:
        return round(ordered[0], 9)
    rank = (len(ordered) - 1) * probability
    lower = math.floor(rank)
    upper = math.ceil(rank)
    if lower == upper:
        return round(ordered[lower], 9)
    fraction = rank - lower
    return round(ordered[lower] * (1 - fraction) + ordered[upper] * fraction, 9)


def distribution(values: list[float]) -> dict[str, Any]:
    return {
        "count": len(values),
        "minimum": None if not values else round(min(values), 9),
        "median": percentile(values, 0.5),
        "p90": percentile(values, 0.9),
        "maximum": None if not values else round(max(values), 9),
        "mean": None if not values else round(statistics.fmean(values), 9),
        "percentileMethod": "linear interpolation over sorted observations",
    }


def analyze_visual(arguments: argparse.Namespace) -> int:
    manifest = read_json_object(arguments.manifest)
    composition = composition_by_id(manifest, arguments.composition_id)
    source_time_base = Fraction(
        int(manifest["source"]["videoTimeBase"]["numerator"]),
        int(manifest["source"]["videoTimeBase"]["denominator"]),
    )
    source_times = load_ticks(arguments.source_pts, source_time_base)
    output_times = load_ticks(arguments.output_pts, parse_fraction(arguments.output_time_base))
    control_offset = float(manifest["visualControl"]["offsetSeconds"])
    width = int(manifest["visualControl"]["comparisonWidth"])
    height = int(manifest["visualControl"]["comparisonHeight"])
    samples: list[dict[str, Any]] = []
    mapping_errors: list[float] = []
    mapped_differences: list[float] = []
    control_differences: list[float] = []

    for sample in composition["reverseMapSamples"]:
        requested_output = float(rational_seconds(sample["requestedOutput"]))
        output_index = bisect.bisect_left(output_times, requested_output)
        if output_index >= len(output_times):
            raise ValueError("No observed output PTS exists at the reverse-map sample.")
        observed_output = output_times[output_index]
        segment, ideal_source = map_output_time(composition, observed_output)
        source_index = bisect.bisect_left(source_times, ideal_source)
        if source_index >= len(source_times):
            raise ValueError("No observed source PTS exists for the reverse-map sample.")
        observed_source = source_times[source_index]
        mapping_error = abs(observed_source - ideal_source)
        control_seconds = observed_source + control_offset
        if control_seconds > source_times[-1]:
            control_seconds = observed_source - control_offset
        mapped_mad, control_mad = frame_comparison(
            arguments.ffmpeg,
            arguments.source_video,
            arguments.output_video,
            composition,
            observed_output,
            observed_source,
            control_seconds,
            width,
            height,
        )
        mapping_errors.append(mapping_error)
        mapped_differences.append(mapped_mad)
        control_differences.append(control_mad)
        samples.append(
            {
                "sampleId": sample["sampleId"],
                "segmentId": segment["segmentId"],
                "requestedOutputSeconds": round(requested_output, 9),
                "observedOutputSeconds": round(observed_output, 9),
                "idealMappedSourceSeconds": round(ideal_source, 9),
                "observedSourceFrameSeconds": round(observed_source, 9),
                "sourceFrameSelectionErrorSeconds": round(mapping_error, 9),
                "controlOffsetSeconds": round(control_seconds - observed_source, 9),
                "mappedMeanAbsoluteDifference": round(mapped_mad, 6),
                "controlMeanAbsoluteDifference": round(control_mad, 6),
                "controlExceedsMapped": control_mad > mapped_mad,
            }
        )

    subtitle_control: dict[str, Any] | None = None
    if composition.get("subtitleEnabled") is True:
        active_requested = float(rational_seconds(composition["subtitleControl"]["activeOutput"]))
        inactive_requested = float(rational_seconds(composition["subtitleControl"]["inactiveOutput"]))
        overlay_differences: list[float] = []
        observed_times: list[float] = []
        for requested in (active_requested, inactive_requested):
            output_index = bisect.bisect_left(output_times, requested)
            if output_index >= len(output_times):
                raise ValueError("Subtitle control has no observed output frame.")
            observed_output = output_times[output_index]
            _, ideal_source = map_output_time(composition, observed_output)
            source_index = bisect.bisect_left(source_times, ideal_source)
            if source_index >= len(source_times):
                raise ValueError("Subtitle control has no observed source frame.")
            observed_source = source_times[source_index]
            expected_bytes = width * height
            output_frame = extract_gray(
                arguments.ffmpeg,
                arguments.output_video,
                observed_output,
                f"scale={width}:{height}:flags=lanczos,format=gray",
                expected_bytes,
            )
            source_frame = extract_gray(
                arguments.ffmpeg,
                arguments.source_video,
                observed_source,
                source_filter(composition, width, height),
                expected_bytes,
            )
            overlay_differences.append(absolute_difference(output_frame, source_frame))
            observed_times.append(observed_output)
        subtitle_control = {
            "activeRequestedOutputSeconds": round(active_requested, 9),
            "activeObservedOutputSeconds": round(observed_times[0], 9),
            "inactiveRequestedOutputSeconds": round(inactive_requested, 9),
            "inactiveObservedOutputSeconds": round(observed_times[1], 9),
            "activeMappedFrameDifference": round(overlay_differences[0], 6),
            "inactiveMappedFrameDifference": round(overlay_differences[1], 6),
            "activeDifferenceExceedsInactive": overlay_differences[0] > overlay_differences[1],
            "subtitleTextContentIncludedInSummary": False,
        }

    maximum_error = float(arguments.maximum_source_frame_duration_seconds)
    report = {
        "status": "succeeded",
        "compositionId": composition["compositionId"],
        "mappingAuthority": "observed-output-and-source-presentation-timestamps-plus-handwritten-piecewise-manifest",
        "outputFrameSelectionRule": "first observed output PTS not earlier than requested output time",
        "sourceFrameSelectionRule": "first observed source PTS not earlier than the ideal piecewise-mapped source time",
        "sourcePtsArtifact": file_descriptor(arguments.source_pts),
        "outputPtsArtifact": file_descriptor(arguments.output_pts),
        "sourcePtsCount": len(source_times),
        "outputPtsCount": len(output_times),
        "sampleCount": len(samples),
        "samples": samples,
        "sourceFrameSelectionErrorSeconds": distribution(mapping_errors),
        "maximumObservedSourceFrameDurationSeconds": maximum_error,
        "allSourceFrameSelectionErrorsWithinObservedMaximumDuration": all(
            value <= maximum_error + 1e-9 for value in mapping_errors
        ),
        "mappedFrameDifference": distribution(mapped_differences),
        "controlFrameDifference": distribution(control_differences),
        "allOffsetControlsExceedMappedDifferences": all(
            sample["controlExceedsMapped"] for sample in samples
        ),
        "visualControlIsContentCorrespondenceNotQualityScore": True,
        "subtitleControl": subtitle_control,
        "sourcePixelsIncludedInSummary": False,
        "productionThresholdsApproved": False,
    }
    print(json.dumps(report))
    return 0


def parse_volume(stderr: str) -> dict[str, float | None]:
    values: dict[str, float | None] = {}
    for name, raw in VOLUME_PATTERN.findall(stderr):
        values[name] = None if raw == "-inf" else float(raw)
    if "mean_volume" not in values or "max_volume" not in values:
        raise RuntimeError("FFmpeg volumedetect did not report mean and maximum volume.")
    return values


def run_process(arguments: list[str], timeout: int) -> subprocess.CompletedProcess[bytes]:
    return subprocess.run(arguments, capture_output=True, timeout=timeout, check=False)


def analyze_audio(arguments: argparse.Namespace) -> int:
    manifest = read_json_object(arguments.manifest)
    composition = composition_by_id(manifest, arguments.composition_id)
    if composition["compositionId"] != "landscape-trim-concat-subtitle-audio":
        raise ValueError("Audio control is defined only for the landscape composition.")
    command = [str(arguments.ffmpeg), "-nostdin", "-v", "info"]
    for segment in composition["segments"]:
        command.extend(
            [
                "-ss",
                f"{float(rational_seconds(segment['sourceStart'])):.9f}",
                "-t",
                f"{float(rational_seconds(segment['sourceDuration'])):.9f}",
                "-i",
                str(arguments.source_video),
            ]
        )
    filters: list[str] = []
    labels: list[str] = []
    for index, segment in enumerate(composition["segments"]):
        duration = float(rational_seconds(segment["sourceDuration"]))
        filters.append(
            f"[{index}:a:0]atrim=start=0:end={duration:.9f},asetpts=PTS-STARTPTS,aresample=48000[a{index}]"
        )
        labels.append(f"[a{index}]")
    filters.append(f"{''.join(labels)}concat=n={len(labels)}:v=0:a=1,volumedetect[reference]")
    command.extend(["-filter_complex", ";".join(filters), "-map", "[reference]", "-f", "null", os.devnull])
    reference = run_process(command, 600)
    if reference.returncode != 0:
        raise RuntimeError("Reference audio volume analysis failed.")
    output = run_process(
        [
            str(arguments.ffmpeg),
            "-nostdin",
            "-v",
            "info",
            "-i",
            str(arguments.output_video),
            "-map",
            "0:a:0",
            "-af",
            "volumedetect",
            "-f",
            "null",
            os.devnull,
        ],
        300,
    )
    if output.returncode != 0:
        raise RuntimeError("Rendered audio volume analysis failed.")
    reference_volume = parse_volume(reference.stderr.decode("utf-8", errors="replace"))
    output_volume = parse_volume(output.stderr.decode("utf-8", errors="replace"))
    if reference_volume["mean_volume"] is None or output_volume["mean_volume"] is None:
        raise RuntimeError("Audio control cannot compare silent input or output.")
    observed_gain = float(output_volume["mean_volume"]) - float(reference_volume["mean_volume"])
    expected_gain = float(manifest["audioMechanism"]["expectedGainDecibels"])
    tolerance = float(manifest["audioMechanism"]["gainToleranceDecibels"])
    report = {
        "status": "succeeded",
        "compositionId": composition["compositionId"],
        "linearGain": float(manifest["audioMechanism"]["linearGain"]),
        "expectedGainDecibels": expected_gain,
        "gainToleranceDecibels": tolerance,
        "referenceMeanVolumeDecibels": reference_volume["mean_volume"],
        "referenceMaximumVolumeDecibels": reference_volume["max_volume"],
        "outputMeanVolumeDecibels": output_volume["mean_volume"],
        "outputMaximumVolumeDecibels": output_volume["max_volume"],
        "observedMeanVolumeGainDecibels": round(observed_gain, 6),
        "absoluteGainErrorDecibels": round(abs(observed_gain - expected_gain), 6),
        "gainMechanismWithinStage0Tolerance": abs(observed_gain - expected_gain) <= tolerance,
        "outputIsNotSilent": output_volume["mean_volume"] is not None,
        "toleranceIsProductionApproved": False,
        "audioSamplesIncludedInSummary": False,
    }
    print(json.dumps(report))
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)

    validate = subparsers.add_parser("validate-manifest")
    validate.add_argument("--manifest", required=True, type=Path)
    validate.add_argument("--source-sha256", required=True)
    validate.add_argument("--source-byte-size", required=True, type=int)
    validate.add_argument("--ffmpeg-sha256", required=True)
    validate.add_argument("--ffprobe-sha256", required=True)
    validate.set_defaults(handler=validate_manifest)

    visual = subparsers.add_parser("analyze-visual")
    visual.add_argument("--manifest", required=True, type=Path)
    visual.add_argument("--composition-id", required=True)
    visual.add_argument("--source-video", required=True, type=Path)
    visual.add_argument("--output-video", required=True, type=Path)
    visual.add_argument("--source-pts", required=True, type=Path)
    visual.add_argument("--output-pts", required=True, type=Path)
    visual.add_argument("--output-time-base", required=True)
    visual.add_argument("--ffmpeg", required=True, type=Path)
    visual.add_argument("--maximum-source-frame-duration-seconds", required=True, type=float)
    visual.set_defaults(handler=analyze_visual)

    audio = subparsers.add_parser("analyze-audio")
    audio.add_argument("--manifest", required=True, type=Path)
    audio.add_argument("--composition-id", required=True)
    audio.add_argument("--source-video", required=True, type=Path)
    audio.add_argument("--output-video", required=True, type=Path)
    audio.add_argument("--ffmpeg", required=True, type=Path)
    audio.set_defaults(handler=analyze_audio)
    return parser


def main() -> int:
    arguments = build_parser().parse_args()
    return int(arguments.handler(arguments))


if __name__ == "__main__":
    sys.exit(main())
