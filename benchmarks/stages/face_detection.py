"""Measures MediaPipe face detection on sampled proxy frames.

Detection runs on sampled frames rather than on every frame because Stage 0 only needs to know
whether faces are found reliably enough to drive vertical reframing, and because ST-003 showed that
touching every source frame is impractical. Every sampled proxy timestamp is converted back to a
source presentation time through the mapping that ST-004 measured.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import statistics
import sys
import time
import urllib.request
import uuid
from bisect import bisect_right
from fractions import Fraction
from pathlib import Path
from typing import TypedDict

import cv2
import mediapipe as mp


DEFAULT_MODEL_URL = (
    "https://storage.googleapis.com/mediapipe-models/face_detector/"
    "blaze_face_short_range/float16/1/blaze_face_short_range.tflite"
)
EXPECTED_MODEL_SHA256 = "b4578f35940bf5a1a655214a1cce5cab13eba73c1297cd78e1a04c2380b0152f"
EXPECTED_MODEL_BYTE_SIZE = 229746
DEFAULT_CONTROL_IMAGE_URL = "https://storage.googleapis.com/mediapipe-assets/portrait.jpg"
EXPECTED_CONTROL_IMAGE_SHA256 = "a6f11efaa834706db23f275b6115058fa87fc7f14362681e6abe14e82749de3e"
EXPECTED_CONTROL_IMAGE_BYTE_SIZE = 176561


class FaceRecord(TypedDict):
    x: int
    y: int
    width: int
    height: int
    widthShareOfFrame: float
    heightShareOfFrame: float
    score: float | None


class FaceObservation(TypedDict):
    proxyFrameIndex: int
    proxySeconds: float
    sourceSeconds: float
    faceCount: int
    faces: list[FaceRecord]


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
    if not values:
        raise ValueError(f"No presentation timestamps were loaded from {csv_path.name}.")
    values.sort()
    seconds = [float(value * time_base) for value in values]
    if any(right <= left for left, right in zip(seconds, seconds[1:])):
        raise ValueError(f"Presentation timestamps are not strictly increasing in {csv_path.name}.")
    return seconds


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def describe_and_verify_asset(
    asset_path: Path,
    asset_url: str,
    downloaded: bool,
    expected_byte_size: int,
    expected_sha256: str,
    label: str,
) -> dict:
    byte_size = asset_path.stat().st_size
    digest = file_sha256(asset_path)
    if byte_size != expected_byte_size or digest != expected_sha256:
        raise ValueError(
            f"{label} does not match the pinned byte size and SHA-256 "
            f"(bytes={byte_size}, sha256={digest})."
        )
    return {
        "fileName": asset_path.name,
        "byteSize": byte_size,
        "sha256": digest,
        "downloadedDuringThisRun": downloaded,
        "sourceUrl": asset_url,
    }


def ensure_asset(
    asset_path: Path,
    asset_url: str,
    expected_byte_size: int,
    expected_sha256: str,
    label: str,
) -> dict:
    downloaded = False
    if asset_path.exists():
        return describe_and_verify_asset(
            asset_path,
            asset_url,
            downloaded,
            expected_byte_size,
            expected_sha256,
            label,
        )

    asset_path.parent.mkdir(parents=True, exist_ok=True)
    staging_path = asset_path.with_name(f".{asset_path.name}.{uuid.uuid4().hex}.tmp")
    try:
        with urllib.request.urlopen(asset_url, timeout=120) as response:  # noqa: S310 - pinned HTTPS asset
            with staging_path.open("xb") as output:
                shutil.copyfileobj(response, output)
        describe_and_verify_asset(
            staging_path,
            asset_url,
            downloaded=True,
            expected_byte_size=expected_byte_size,
            expected_sha256=expected_sha256,
            label=label,
        )
        os.replace(staging_path, asset_path)
        downloaded = True
    finally:
        staging_path.unlink(missing_ok=True)

    return describe_and_verify_asset(
        asset_path,
        asset_url,
        downloaded,
        expected_byte_size,
        expected_sha256,
        label,
    )


def ensure_model(model_path: Path, model_url: str) -> dict:
    return ensure_asset(
        model_path,
        model_url,
        EXPECTED_MODEL_BYTE_SIZE,
        EXPECTED_MODEL_SHA256,
        "Face detector model",
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--proxy-video", required=True, type=Path)
    parser.add_argument("--source-packets", required=True, type=Path)
    parser.add_argument("--source-time-base", required=True)
    parser.add_argument("--proxy-packets", required=True, type=Path)
    parser.add_argument("--proxy-time-base", required=True)
    parser.add_argument("--model-path", required=True, type=Path)
    parser.add_argument("--model-url", default=DEFAULT_MODEL_URL)
    parser.add_argument("--control-image-path", required=True, type=Path)
    parser.add_argument("--control-image-url", default=DEFAULT_CONTROL_IMAGE_URL)
    parser.add_argument("--sample-count", type=int, default=300)
    parser.add_argument("--min-confidence", type=float, default=0.5)
    arguments = parser.parse_args()

    if arguments.sample_count <= 0:
        raise ValueError("sample-count must be greater than zero.")
    if not 0.0 <= arguments.min_confidence <= 1.0:
        raise ValueError("min-confidence must be between zero and one.")

    model = ensure_model(arguments.model_path, arguments.model_url)
    control_asset = ensure_asset(
        arguments.control_image_path,
        arguments.control_image_url,
        EXPECTED_CONTROL_IMAGE_BYTE_SIZE,
        EXPECTED_CONTROL_IMAGE_SHA256,
        "Face detector positive-control image",
    )
    control_bgr = cv2.imread(str(arguments.control_image_path), cv2.IMREAD_COLOR)
    if control_bgr is None:
        raise ValueError("The pinned positive-control image could not be decoded.")
    control_rgb = cv2.cvtColor(control_bgr, cv2.COLOR_BGR2RGB)
    control_image = mp.Image(image_format=mp.ImageFormat.SRGB, data=control_rgb)
    control_options = mp.tasks.vision.FaceDetectorOptions(
        base_options=mp.tasks.BaseOptions(model_asset_path=str(arguments.model_path)),
        running_mode=mp.tasks.vision.RunningMode.VIDEO,
        min_detection_confidence=arguments.min_confidence,
    )
    control_started = time.perf_counter()
    with mp.tasks.vision.FaceDetector.create_from_options(control_options) as control_detector:
        control_result = control_detector.detect_for_video(control_image, 0)
    control_detection_seconds = time.perf_counter() - control_started
    control_scores = [
        float(detection.categories[0].score)
        for detection in control_result.detections
        if detection.categories
    ]
    if not control_result.detections:
        raise RuntimeError("The pinned positive-control image did not produce a face detection.")

    source_times = load_presentation_seconds(arguments.source_packets, parse_rational(arguments.source_time_base))
    proxy_times = load_presentation_seconds(arguments.proxy_packets, parse_rational(arguments.proxy_time_base))

    capture = cv2.VideoCapture(str(arguments.proxy_video))
    try:
        if not capture.isOpened():
            raise RuntimeError("The proxy video could not be opened for sampling.")
        frame_count = int(capture.get(cv2.CAP_PROP_FRAME_COUNT))
        width = int(capture.get(cv2.CAP_PROP_FRAME_WIDTH))
        height = int(capture.get(cv2.CAP_PROP_FRAME_HEIGHT))
        if frame_count <= 0 or width <= 0 or height <= 0:
            raise ValueError("The proxy did not report usable frame dimensions and count.")
        if len(proxy_times) != frame_count:
            raise ValueError(
                "Observed proxy presentation timestamp count does not match the decoded frame count "
                f"({len(proxy_times)} != {frame_count})."
            )

        sample_count = min(arguments.sample_count, frame_count)
        indices = [
            int(round(step * (frame_count - 1) / max(1, sample_count - 1)))
            for step in range(sample_count)
        ]
        if len(set(indices)) != len(indices):
            raise ValueError("The stratified sampling rule produced duplicate frame indices.")

        base_options = mp.tasks.BaseOptions(model_asset_path=str(arguments.model_path))
        options = mp.tasks.vision.FaceDetectorOptions(
            base_options=base_options,
            running_mode=mp.tasks.vision.RunningMode.VIDEO,
            min_detection_confidence=arguments.min_confidence,
        )

        observations: list[FaceObservation] = []
        detection_seconds = 0.0
        decode_failures = 0
        with mp.tasks.vision.FaceDetector.create_from_options(options) as detector:
            previous_timestamp_ms = -1
            for index in indices:
                capture.set(cv2.CAP_PROP_POS_FRAMES, index)
                ok, frame = capture.read()
                if not ok or frame is None:
                    decode_failures += 1
                    continue
                rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
                image = mp.Image(image_format=mp.ImageFormat.SRGB, data=rgb)
                proxy_seconds = proxy_times[index]
                timestamp_ms = round(proxy_seconds * 1000)
                if timestamp_ms <= previous_timestamp_ms:
                    raise ValueError("Sample timestamps are not strictly increasing in milliseconds.")
                previous_timestamp_ms = timestamp_ms

                started = time.perf_counter()
                result = detector.detect_for_video(image, timestamp_ms)
                detection_seconds += time.perf_counter() - started

                source_index = max(0, bisect_right(source_times, proxy_seconds) - 1)
                faces: list[FaceRecord] = []
                for detection in result.detections:
                    box = detection.bounding_box
                    score = float(detection.categories[0].score) if detection.categories else None
                    faces.append(
                        {
                            "x": int(box.origin_x),
                            "y": int(box.origin_y),
                            "width": int(box.width),
                            "height": int(box.height),
                            "widthShareOfFrame": round(box.width / width, 6),
                            "heightShareOfFrame": round(box.height / height, 6),
                            "score": None if score is None else round(score, 6),
                        }
                    )

                observations.append(
                    {
                        "proxyFrameIndex": index,
                        "proxySeconds": round(proxy_seconds, 6),
                        "sourceSeconds": round(source_times[source_index], 6),
                        "faceCount": len(faces),
                        "faces": faces,
                    }
                )
    finally:
        capture.release()

    with_faces = [item for item in observations if item["faceCount"] > 0]
    face_widths = [face["widthShareOfFrame"] for item in observations for face in item["faces"]]
    scores = [face["score"] for item in observations for face in item["faces"] if face["score"] is not None]
    counts = [item["faceCount"] for item in observations]

    report = {
        "proxy": {
            "reportedFrameCount": frame_count,
            "width": width,
            "height": height,
        },
        "model": model,
        "control": {
            "purpose": "Confirm that the pinned model and VIDEO-mode pipeline detect a known positive image before interpreting fixture results.",
            "asset": control_asset,
            "runningMode": "VIDEO",
            "timestampMilliseconds": 0,
            "detectedFaceCount": len(control_result.detections),
            "scores": [round(score, 6) for score in control_scores],
            "detectionMilliseconds": round(control_detection_seconds * 1000, 3),
            "passed": True,
        },
        "sampling": {
            "requestedSampleCount": arguments.sample_count,
            "attemptedSampleCount": len(indices),
            "decodedSampleCount": len(observations),
            "decodeFailureCount": decode_failures,
            "sampleShareOfProxyFrames": round(len(indices) / frame_count, 6),
            "indexRule": "Round evenly spaced positions from the first through the last reported proxy frame.",
            "firstProxyFrameIndex": indices[0],
            "lastProxyFrameIndex": indices[-1],
            "minDetectionConfidence": arguments.min_confidence,
            "runningMode": "VIDEO",
        },
        "detection": {
            "totalDetectionSeconds": round(detection_seconds, 3),
            "meanDetectionMillisecondsPerFrame": round(detection_seconds * 1000 / len(observations), 3) if observations else None,
            "framesWithAtLeastOneFace": len(with_faces),
            "framesWithNoFace": len(observations) - len(with_faces),
            "framesWithMultipleFaces": sum(1 for count in counts if count > 1),
            "detectionRate": round(len(with_faces) / len(observations), 6) if observations else None,
            "detectedFaceCount": sum(counts),
            "maxFacesInOneFrame": max(counts) if counts else 0,
            "meanFacesPerFrame": round(statistics.fmean(counts), 4) if counts else None,
            "medianFaceWidthShareOfFrame": round(statistics.median(face_widths), 6) if face_widths else None,
            "minFaceWidthShareOfFrame": round(min(face_widths), 6) if face_widths else None,
            "maxFaceWidthShareOfFrame": round(max(face_widths), 6) if face_widths else None,
            "medianScore": round(statistics.median(scores), 6) if scores else None,
            "minScore": round(min(scores), 6) if scores else None,
            "maxScore": round(max(scores), 6) if scores else None,
        },
        "observations": observations,
        "privacy": {
            "imagePixelsPersisted": False,
            "observationsContainSourceTimesAndFaceBoundingBoxes": True,
            "positiveControlStoredOnlyInIgnoredBenchmarkWork": True,
        },
    }

    json.dump(report, sys.stdout, ensure_ascii=False)
    return 0


if __name__ == "__main__":
    sys.exit(main())
