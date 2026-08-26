"""Detects shot boundaries on the proxy and reports them on the source presentation timeline.

Boundaries are detected on the constant-rate proxy because a full source decode is impractical, and
each detected proxy timestamp is converted back through the same observed-timestamp mapping that
ST-004 measured, so that a boundary is reported as a source time rather than as a proxy frame index.
"""

from __future__ import annotations

import argparse
import json
import statistics
import sys
import time
from bisect import bisect_right
from fractions import Fraction
from pathlib import Path

from scenedetect import AdaptiveDetector, ContentDetector, SceneManager, open_video


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


def detect(video_path: Path, detector_name: str, threshold: float) -> tuple[list[tuple[float, float]], float]:
    video = open_video(str(video_path))
    manager = SceneManager()
    if detector_name == "content":
        manager.add_detector(ContentDetector(threshold=threshold))
    elif detector_name == "adaptive":
        manager.add_detector(AdaptiveDetector(adaptive_threshold=threshold))
    else:
        raise ValueError(f"Unsupported detector: {detector_name}")

    started = time.perf_counter()
    manager.detect_scenes(video, show_progress=False)
    elapsed = time.perf_counter() - started
    scenes = manager.get_scene_list()
    return [(scene[0].get_seconds(), scene[1].get_seconds()) for scene in scenes], elapsed


def summarise(
    detector_name: str,
    threshold: float,
    scenes: list[tuple[float, float]],
    elapsed: float,
    source_times: list[float],
    proxy_times: list[float],
) -> dict:
    boundaries = [start for start, _ in scenes][1:]
    durations = [end - start for start, end in scenes]

    mapped = []
    for boundary in boundaries:
        proxy_index = max(0, bisect_right(proxy_times, boundary) - 1)
        source_index = max(0, bisect_right(source_times, proxy_times[proxy_index]) - 1)
        mapped.append(
            {
                "proxySeconds": round(boundary, 6),
                "proxyFrameIndex": proxy_index,
                "sourceSeconds": round(source_times[source_index], 6),
                "sourceFrameIndex": source_index,
            }
        )

    return {
        "detector": detector_name,
        "threshold": threshold,
        "elapsedSeconds": round(elapsed, 3),
        "sceneCount": len(scenes),
        "boundaryCount": len(boundaries),
        "shortestSceneSeconds": round(min(durations), 6) if durations else None,
        "longestSceneSeconds": round(max(durations), 6) if durations else None,
        "medianSceneSeconds": round(statistics.median(durations), 6) if durations else None,
        "boundariesUnderTwoSeconds": sum(1 for value in durations if value < 2.0),
        "firstBoundaries": mapped[:20],
        "allBoundarySourceSeconds": [item["sourceSeconds"] for item in mapped],
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--proxy-video", required=True, type=Path)
    parser.add_argument("--source-packets", required=True, type=Path)
    parser.add_argument("--source-time-base", required=True)
    parser.add_argument("--proxy-packets", required=True, type=Path)
    parser.add_argument("--proxy-time-base", required=True)
    arguments = parser.parse_args()

    source_times = load_presentation_seconds(arguments.source_packets, parse_rational(arguments.source_time_base))
    proxy_times = load_presentation_seconds(arguments.proxy_packets, parse_rational(arguments.proxy_time_base))

    configurations = [
        ("content", 27.0),
        ("content", 40.0),
        ("adaptive", 3.0),
    ]

    detectors = []
    for detector_name, threshold in configurations:
        scenes, elapsed = detect(arguments.proxy_video, detector_name, threshold)
        detectors.append(summarise(detector_name, threshold, scenes, elapsed, source_times, proxy_times))

    agreement = None
    content_default = next((item for item in detectors if item["detector"] == "content" and item["threshold"] == 27.0), None)
    adaptive = next((item for item in detectors if item["detector"] == "adaptive"), None)
    if content_default and adaptive:
        left = content_default["allBoundarySourceSeconds"]
        right = adaptive["allBoundarySourceSeconds"]
        tolerance = 0.5
        matched = sum(1 for value in left if any(abs(value - other) <= tolerance for other in right))
        agreement = {
            "toleranceSeconds": tolerance,
            "contentBoundaryCount": len(left),
            "adaptiveBoundaryCount": len(right),
            "contentBoundariesMatchedByAdaptive": matched,
        }

    json.dump(
        {
            "detectors": detectors,
            "agreement": agreement,
            "note": "Boundaries were detected on the proxy and converted to source presentation times through the observed-timestamp mapping.",
        },
        sys.stdout,
        ensure_ascii=False,
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
