"""Builds and measures the mapping between a constant-rate proxy and the source presentation timestamps.

The source of this project is variable frame timing, so a proxy frame index cannot be converted to a
source time by dividing by a nominal frame rate. This module derives the mapping from the timestamps
that were actually observed in both files, measures the round-trip quantisation error an editor would
experience, and checks that the mapped frames really show the same picture.
"""

from __future__ import annotations

import argparse
import json
import statistics
import subprocess
import sys
import tempfile
from bisect import bisect_right
from fractions import Fraction
from pathlib import Path

import cv2
import numpy as np


def parse_rational(text: str) -> Fraction:
    numerator, _, denominator = text.partition("/")
    if not denominator:
        return Fraction(int(numerator), 1)
    return Fraction(int(numerator), int(denominator))


def load_presentation_seconds(csv_path: Path, time_base: Fraction) -> list[float]:
    values: list[int] = []
    with csv_path.open("r", encoding="utf-8") as handle:
        for line in handle:
            token = line.strip().split(",")[0]
            if not token or token == "N/A":
                continue
            values.append(int(token))
    values.sort()
    return [float(value * time_base) for value in values]


def build_mapping(proxy_times: list[float], source_times: list[float]) -> list[int]:
    """Maps every proxy frame to the source frame that is being presented at that instant."""
    mapping: list[int] = []
    for proxy_time in proxy_times:
        index = bisect_right(source_times, proxy_time) - 1
        mapping.append(max(index, 0))
    return mapping


def extract_frame(ffmpeg: Path, video: Path, seconds: float, destination: Path) -> bool:
    completed = subprocess.run(
        [
            str(ffmpeg), "-nostdin", "-v", "error",
            "-ss", f"{seconds:.6f}", "-i", str(video),
            "-frames:v", "1", "-y", str(destination),
        ],
        capture_output=True,
        text=True,
        timeout=300,
        check=False,
    )
    return completed.returncode == 0 and destination.exists() and destination.stat().st_size > 0


def mean_absolute_difference(first: Path, second: Path, width: int, height: int) -> float | None:
    left = cv2.imread(str(first), cv2.IMREAD_GRAYSCALE)
    right = cv2.imread(str(second), cv2.IMREAD_GRAYSCALE)
    if left is None or right is None:
        return None
    left = cv2.resize(left, (width, height), interpolation=cv2.INTER_AREA)
    right = cv2.resize(right, (width, height), interpolation=cv2.INTER_AREA)
    return float(np.mean(cv2.absdiff(left, right)))


def percentile(values: list[float], fraction: float) -> float:
    if not values:
        return 0.0
    ordered = sorted(values)
    index = min(len(ordered) - 1, max(0, int(round(fraction * (len(ordered) - 1)))))
    return ordered[index]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-packets", required=True, type=Path)
    parser.add_argument("--source-time-base", required=True)
    parser.add_argument("--proxy-packets", required=True, type=Path)
    parser.add_argument("--proxy-time-base", required=True)
    parser.add_argument("--source-video", required=True, type=Path)
    parser.add_argument("--proxy-video", required=True, type=Path)
    parser.add_argument("--ffmpeg", required=True, type=Path)
    parser.add_argument("--visual-samples", type=int, default=10)
    parser.add_argument("--control-offset-seconds", type=float, default=5.0)
    parser.add_argument("--compare-width", type=int, default=320)
    parser.add_argument("--compare-height", type=int, default=180)
    arguments = parser.parse_args()

    source_times = load_presentation_seconds(arguments.source_packets, parse_rational(arguments.source_time_base))
    proxy_times = load_presentation_seconds(arguments.proxy_packets, parse_rational(arguments.proxy_time_base))
    if not source_times or not proxy_times:
        raise SystemExit("Both the source and the proxy must expose presentation timestamps.")

    mapping = build_mapping(proxy_times, source_times)
    distinct_referenced = len(set(mapping))
    repeated_proxy_frames = sum(
        1 for position in range(1, len(mapping)) if mapping[position] == mapping[position - 1]
    )
    proxy_frames_before_first_source_frame = sum(1 for time in proxy_times if time < source_times[0])

    # Every source frame time is a possible cut point, so the error is measured across all of them.
    frame_duration = proxy_times[1] - proxy_times[0] if len(proxy_times) > 1 else 0.0
    round_trip_errors: list[float] = []
    unrepresentable = 0
    for source_time in source_times:
        if frame_duration <= 0:
            break
        position = bisect_right(proxy_times, source_time) - 1
        if position < 0:
            unrepresentable += 1
            continue
        recovered = source_times[mapping[position]]
        round_trip_errors.append(abs(source_time - recovered))

    referenced = sorted(set(mapping))
    boundary_points = []
    for label, source_index in (
        ("first-source-frame", 0),
        ("last-source-frame", len(source_times) - 1),
    ):
        source_time = source_times[source_index]
        position = max(0, bisect_right(proxy_times, source_time) - 1)
        boundary_points.append(
            {
                "label": label,
                "sourceSeconds": round(source_time, 6),
                "proxyFrameIndex": position,
                "proxySeconds": round(proxy_times[position], 6),
                "recoveredSourceSeconds": round(source_times[mapping[position]], 6),
                "errorSeconds": round(abs(source_time - source_times[mapping[position]]), 6),
            }
        )

    stratified = []
    sample_count = max(2, arguments.visual_samples)
    for step in range(sample_count):
        source_index = int(round(step * (len(source_times) - 1) / (sample_count - 1)))
        source_time = source_times[source_index]
        position = max(0, bisect_right(proxy_times, source_time) - 1)
        stratified.append(
            {
                "sourceIndex": source_index,
                "sourceSeconds": source_time,
                "proxyFrameIndex": position,
                "proxySeconds": proxy_times[position],
                "recoveredSourceSeconds": source_times[mapping[position]],
            }
        )

    visual_samples = []
    with tempfile.TemporaryDirectory() as scratch:
        scratch_path = Path(scratch)
        for order, point in enumerate(stratified):
            proxy_image = scratch_path / f"proxy-{order}.png"
            source_image = scratch_path / f"source-{order}.png"
            control_image = scratch_path / f"control-{order}.png"
            if not extract_frame(arguments.ffmpeg, arguments.proxy_video, point["proxySeconds"], proxy_image):
                continue
            if not extract_frame(arguments.ffmpeg, arguments.source_video, point["recoveredSourceSeconds"], source_image):
                continue
            matched = mean_absolute_difference(
                proxy_image, source_image, arguments.compare_width, arguments.compare_height
            )
            # Offsetting towards the end of the file collapses to the same frame for the last sample,
            # so the control point is taken backwards whenever the forward offset would be clamped.
            forward_seconds = point["recoveredSourceSeconds"] + arguments.control_offset_seconds
            if forward_seconds <= source_times[-1]:
                control_seconds = forward_seconds
            else:
                control_seconds = max(source_times[0], point["recoveredSourceSeconds"] - arguments.control_offset_seconds)
            control = None
            if extract_frame(arguments.ffmpeg, arguments.source_video, control_seconds, control_image):
                control = mean_absolute_difference(
                    proxy_image, control_image, arguments.compare_width, arguments.compare_height
                )
            visual_samples.append(
                {
                    "proxyFrameIndex": point["proxyFrameIndex"],
                    "proxySeconds": round(point["proxySeconds"], 6),
                    "mappedSourceSeconds": round(point["recoveredSourceSeconds"], 6),
                    "meanAbsoluteDifference": None if matched is None else round(matched, 4),
                    "controlOffsetSeconds": round(control_seconds - point["recoveredSourceSeconds"], 6),
                    "controlMeanAbsoluteDifference": None if control is None else round(control, 4),
                    "controlExceedsMapped": None if (matched is None or control is None) else bool(control > matched),
                }
            )

    matched_values = [item["meanAbsoluteDifference"] for item in visual_samples if item["meanAbsoluteDifference"] is not None]
    control_values = [item["controlMeanAbsoluteDifference"] for item in visual_samples if item["controlMeanAbsoluteDifference"] is not None]
    comparable = [item for item in visual_samples if item["controlExceedsMapped"] is not None]

    report = {
        "sourceTiming": {
            "timeBase": arguments.source_time_base,
            "packetCount": len(source_times),
            "minPresentationSeconds": round(source_times[0], 6),
            "maxPresentationSeconds": round(source_times[-1], 6),
        },
        "proxyTiming": {
            "timeBase": arguments.proxy_time_base,
            "packetCount": len(proxy_times),
            "minPresentationSeconds": round(proxy_times[0], 6),
            "maxPresentationSeconds": round(proxy_times[-1], 6),
            "observedFrameDurationSeconds": round(frame_duration, 9),
        },
        "mapping": {
            "rule": "A proxy frame maps to the last source frame whose presentation timestamp is not later than the proxy frame timestamp.",
            "proxyFrameCount": len(proxy_times),
            "sourceFrameCount": len(source_times),
            "distinctSourceFramesReferenced": distinct_referenced,
            "sourceFramesNeverReferenced": len(source_times) - distinct_referenced,
            "proxyFramesRepeatingPreviousSourceFrame": repeated_proxy_frames,
            "proxyFramesBeforeFirstSourceFrame": proxy_frames_before_first_source_frame,
            "firstReferencedSourceIndex": referenced[0],
            "lastReferencedSourceIndex": referenced[-1],
        },
        "roundTrip": {
            "definition": "Each source frame time is treated as a candidate cut point, converted to the proxy frame that displays it, then converted back through the mapping.",
            "testedPointCount": len(round_trip_errors),
            "unrepresentablePointCount": unrepresentable,
            "errorSecondsMean": round(statistics.fmean(round_trip_errors), 9) if round_trip_errors else None,
            "errorSecondsMedian": round(statistics.median(round_trip_errors), 9) if round_trip_errors else None,
            "errorSecondsP90": round(percentile(round_trip_errors, 0.90), 9),
            "errorSecondsP99": round(percentile(round_trip_errors, 0.99), 9),
            "errorSecondsMax": round(max(round_trip_errors), 9) if round_trip_errors else None,
            "boundaryPoints": boundary_points,
        },
        "visualCorrespondence": {
            "metric": "Mean absolute difference of luminance after both frames are resized to a common comparison size.",
            "comparisonWidth": arguments.compare_width,
            "comparisonHeight": arguments.compare_height,
            "sampleCount": len(visual_samples),
            "comparableSampleCount": len(comparable),
            "samplesWhereControlExceedsMapped": sum(1 for item in comparable if item["controlExceedsMapped"]),
            "mappedMeanAbsoluteDifferenceMean": round(statistics.fmean(matched_values), 4) if matched_values else None,
            "mappedMeanAbsoluteDifferenceMax": round(max(matched_values), 4) if matched_values else None,
            "controlMeanAbsoluteDifferenceMean": round(statistics.fmean(control_values), 4) if control_values else None,
            "controlMeanAbsoluteDifferenceMin": round(min(control_values), 4) if control_values else None,
            "controlSeparatesFromMapped": bool(
                matched_values and control_values and max(matched_values) < min(control_values)
            ),
            "samples": visual_samples,
        },
    }

    json.dump(report, sys.stdout, ensure_ascii=False)
    return 0


if __name__ == "__main__":
    sys.exit(main())
