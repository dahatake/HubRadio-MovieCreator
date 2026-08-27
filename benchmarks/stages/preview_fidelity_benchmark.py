"""Validate and compare ST-013 WebView2 and FFmpeg preview evidence without publishing pixels."""

from __future__ import annotations

import argparse
import bisect
import hashlib
import json
import math
import statistics
import subprocess
import sys
from fractions import Fraction
from pathlib import Path
from typing import Any

import cv2
import numpy as np


EXPECTED_MANIFEST_ID = "st-013-preview-fidelity-v1"
EXPECTED_PROFILE = (1280, 720, "30000/1001", "yuv420p")
EXPECTED_MODES = ("canvas", "svg")
EXPECTED_SAMPLE_IDS = ("inactive-before", "active-middle", "inactive-after")


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


def string_sha256(value: str) -> str:
    return hashlib.sha256(value.encode("utf-8")).hexdigest()


def file_descriptor(path: Path) -> dict[str, Any]:
    return {
        "fileName": path.name,
        "byteSize": path.stat().st_size,
        "sha256": file_sha256(path),
    }


def rational(value: dict[str, Any]) -> Fraction:
    numerator = int(value["value"])
    denominator = int(value["rate"])
    if denominator <= 0:
        raise ValueError("Rational rate must be positive.")
    return Fraction(numerator, denominator)


def pixel(value: dict[str, Any], extent: int) -> int:
    result = rational(value) * extent
    if result.denominator != 1:
        raise ValueError("Normalized layout coordinate does not resolve to an integer pixel.")
    return int(result)


def pixel_rectangle(value: dict[str, Any], width: int, height: int) -> dict[str, int]:
    rectangle = {
        "x": pixel(value["x"], width),
        "y": pixel(value["y"], height),
        "width": pixel(value["width"], width),
        "height": pixel(value["height"], height),
    }
    if (
        rectangle["x"] < 0
        or rectangle["y"] < 0
        or rectangle["width"] <= 0
        or rectangle["height"] <= 0
        or rectangle["x"] + rectangle["width"] > width
        or rectangle["y"] + rectangle["height"] > height
    ):
        raise ValueError("Layout rectangle exceeds the render profile.")
    return rectangle


def validate_manifest_object(manifest: dict[str, Any]) -> dict[str, Any]:
    if manifest.get("schemaVersion") != "1.0.0" or manifest.get("manifestId") != EXPECTED_MANIFEST_ID:
        raise ValueError("Unexpected ST-013 manifest identity.")
    rights = manifest["rights"]
    if (
        rights["status"] != "unconfirmed"
        or rights["allowedUse"] != "local-technical-evaluation-only"
        or rights["externalPublicationAllowed"] is not False
        or rights["cueTextSourceTranscriptDerived"] is not False
    ):
        raise ValueError("The ST-013 manifest does not preserve the fixture rights boundary.")
    profile = manifest["profile"]
    observed_profile = (
        int(profile["width"]),
        int(profile["height"]),
        str(profile["frameRate"]),
        str(profile["pixelFormat"]),
    )
    if observed_profile != EXPECTED_PROFILE or rational(profile["duration"]) != Fraction(2):
        raise ValueError("Unexpected ST-013 render profile.")
    if (
        manifest["sourceFrame"]["sourceTime"] != {"value": 37500, "rate": 600}
        or manifest["sourceFrame"]["exactSourceFrameSelectionAccuracyMeasured"] is not False
    ):
        raise ValueError("The source-frame mechanism control changed or overstates selection accuracy.")

    palette = manifest["palette"]
    if palette["theme"] != "light" or set(palette["roles"].values()) - set(palette["resolvedLight"]):
        raise ValueError("The preview palette does not resolve every declared role.")
    for variable_name, color in palette["resolvedLight"].items():
        if not str(variable_name).startswith("--cp-") or not isinstance(color, str) or len(color) != 7 or not color.startswith("#"):
            raise ValueError("The preview palette contains an invalid variable or resolved color.")

    width, height = observed_profile[:2]
    layout = manifest["layoutSpec"]
    if (
        layout["layoutSpecId"] != "preview-layout-landscape-v1"
        or layout["coordinateSystem"] != "normalized-rational"
        or layout["pixelRounding"] != "exact-integer-products-required"
    ):
        raise ValueError("Unexpected shared LayoutSpec identity or coordinate rule.")
    rectangles = {
        "safeFrame": pixel_rectangle(layout["safeFrame"], width, height),
        "topicPanel": pixel_rectangle(layout["topicPanel"], width, height),
        "topicAccent": pixel_rectangle(layout["topicAccent"], width, height),
        "cueBox": pixel_rectangle(layout["cueBox"], width, height),
    }
    if rectangles != {
        "safeFrame": {"x": 48, "y": 27, "width": 1184, "height": 666},
        "topicPanel": {"x": 64, "y": 50, "width": 448, "height": 90},
        "topicAccent": {"x": 64, "y": 50, "width": 12, "height": 90},
        "cueBox": {"x": 160, "y": 540, "width": 960, "height": 108},
    }:
        raise ValueError("Shared normalized rectangles changed unexpectedly.")
    for label_name in ("topicLabel", "cueText"):
        label = layout[label_name]
        if (
            label["fontFamily"] != "Segoe UI"
            or label["fontFileName"] != "segoeuib.ttf"
            or int(label["fontSizePixels"]) <= 0
        ):
            raise ValueError("The preview font contract changed unexpectedly.")

    cues = manifest["cues"]
    if not isinstance(cues, list) or len(cues) != 1:
        raise ValueError("ST-013 requires exactly one neutral Cue.")
    cue = cues[0]
    cue_start = rational(cue["start"])
    cue_end = rational(cue["end"])
    if (
        cue["cueId"] != "neutral-preview-cue"
        or cue_start != Fraction(1, 2)
        or cue_end != Fraction(3, 2)
        or cue_start >= cue_end
        or cue["endExclusive"] is not True
        or cue["text"] != "PREVIEW CONTROL"
        or cue["sourceTranscriptDerived"] is not False
        or cue["reviewRequired"] is not True
    ):
        raise ValueError("The neutral Cue changed or became source-derived.")

    samples = manifest["samples"]
    if not isinstance(samples, list) or tuple(item["sampleId"] for item in samples) != EXPECTED_SAMPLE_IDS:
        raise ValueError("ST-013 requires the fixed before, active, and after samples.")
    sample_summary: list[dict[str, Any]] = []
    duration = rational(profile["duration"])
    for item in samples:
        timestamp = rational(item["outputTime"])
        if timestamp < 0 or timestamp >= duration:
            raise ValueError("A preview sample falls outside the interval.")
        expected = cue_start <= timestamp < cue_end
        if item["cueExpected"] is not expected:
            raise ValueError("A preview sample contradicts Cue timing.")
        sample_summary.append(
            {
                "sampleId": item["sampleId"],
                "outputTime": item["outputTime"],
                "cueExpected": expected,
            }
        )
    comparison = manifest["comparison"]
    if (
        tuple(comparison["webRenderModes"]) != EXPECTED_MODES
        or comparison["faithfulRenderer"] != "ffmpeg"
        or comparison["exactPixelEqualityRequired"] is not False
        or comparison["perceptualQualityMeasured"] is not False
        or comparison["movingVideoPlaybackMeasured"] is not False
        or comparison["audioPreviewMeasured"] is not False
        or comparison["productionThresholdsApproved"] is not False
    ):
        raise ValueError("The ST-013 comparison scope changed or overstates approval.")
    return {
        "manifestId": manifest["manifestId"],
        "layoutSpecId": layout["layoutSpecId"],
        "profileId": profile["profileId"],
        "rectangles": rectangles,
        "sampleCount": len(samples),
        "samples": sample_summary,
        "webRenderModes": list(EXPECTED_MODES),
        "cueTextSha256": string_sha256(cue["text"]),
        "cueTextCharacterCount": len(cue["text"]),
        "cueTextIncludedInValidationOutput": False,
        "productionThresholdsApproved": False,
    }


def validate_manifest(arguments: argparse.Namespace) -> int:
    manifest = read_json_object(arguments.manifest)
    result = validate_manifest_object(manifest)
    result.update({"status": "succeeded", "manifestSha256": file_sha256(arguments.manifest)})
    print(json.dumps(result))
    return 0


def run_process(command: list[str], timeout: int = 300) -> subprocess.CompletedProcess[bytes]:
    return subprocess.run(command, capture_output=True, check=False, timeout=timeout)


def load_image(path: Path, expected_width: int, expected_height: int) -> np.ndarray:
    image = cv2.imread(str(path), cv2.IMREAD_COLOR)
    if image is None or image.shape != (expected_height, expected_width, 3):
        raise ValueError(f"Image dimensions or channels are invalid: {path.name}")
    return image


def observed_frame_times(ffprobe: Path, video: Path) -> list[float]:
    completed = run_process(
        [
            str(ffprobe),
            "-v",
            "error",
            "-select_streams",
            "v:0",
            "-show_frames",
            "-show_entries",
            "frame=best_effort_timestamp_time",
            "-of",
            "csv=p=0",
            str(video),
        ]
    )
    if completed.returncode != 0:
        raise RuntimeError("FFprobe could not enumerate faithful-render frame times.")
    values: list[float] = []
    for line in completed.stdout.decode("utf-8", errors="strict").splitlines():
        token = line.strip().split(",")[0]
        if token and token != "N/A":
            values.append(float(token))
    if not values or any(not math.isfinite(value) for value in values) or values != sorted(values):
        raise ValueError("Faithful-render frame times are empty, non-finite, or non-monotonic.")
    return values


def extract_frame_by_index(
    ffmpeg: Path,
    video: Path,
    frame_index: int,
    width: int,
    height: int,
    destination: Path,
) -> np.ndarray:
    completed = run_process(
        [
            str(ffmpeg),
            "-nostdin",
            "-v",
            "error",
            "-i",
            str(video),
            "-vf",
            f"select=eq(n\\,{frame_index})",
            "-frames:v",
            "1",
            "-vsync",
            "0",
            "-f",
            "rawvideo",
            "-pix_fmt",
            "bgr24",
            "pipe:1",
        ]
    )
    expected_size = width * height * 3
    if completed.returncode != 0 or len(completed.stdout) != expected_size:
        raise RuntimeError("FFmpeg could not extract the selected faithful-render frame.")
    image = np.frombuffer(completed.stdout, dtype=np.uint8).reshape((height, width, 3)).copy()
    if not cv2.imwrite(str(destination), image):
        raise RuntimeError("The faithful-render comparison frame could not be persisted locally.")
    return image


def percentile(values: np.ndarray, probability: float) -> float:
    return float(np.percentile(values, probability * 100, method="linear"))


def difference_metrics(left: np.ndarray, right: np.ndarray) -> dict[str, Any]:
    if left.shape != right.shape or left.size == 0:
        raise ValueError("Comparison images do not share a usable shape.")
    channel_difference = np.abs(left.astype(np.int16) - right.astype(np.int16)).astype(np.uint8)
    per_pixel_maximum = channel_difference.max(axis=2)
    gray_left = cv2.cvtColor(left, cv2.COLOR_BGR2GRAY)
    gray_right = cv2.cvtColor(right, cv2.COLOR_BGR2GRAY)
    gray_difference = np.abs(gray_left.astype(np.int16) - gray_right.astype(np.int16))
    return {
        "pixelCount": int(per_pixel_maximum.size),
        "exactlyEqualPixelCount": int(np.count_nonzero(per_pixel_maximum == 0)),
        "differingPixelCount": int(np.count_nonzero(per_pixel_maximum != 0)),
        "differingPixelFraction": round(float(np.count_nonzero(per_pixel_maximum) / per_pixel_maximum.size), 9),
        "absoluteChannelDifference": {
            "mean": round(float(channel_difference.mean()), 6),
            "median": round(percentile(channel_difference, 0.5), 6),
            "p90": round(percentile(channel_difference, 0.9), 6),
            "p99": round(percentile(channel_difference, 0.99), 6),
            "maximum": int(channel_difference.max()),
        },
        "grayscaleMeanAbsoluteDifference": round(float(gray_difference.mean()), 6),
        "exactPixelEqualityObserved": bool(np.count_nonzero(per_pixel_maximum) == 0),
        "rawEightBitDifferencesArePerceptualQualityEvidence": False,
    }


def region_mean_difference(left: np.ndarray, right: np.ndarray, rectangle: dict[str, int]) -> dict[str, float]:
    difference = np.abs(left.astype(np.int16) - right.astype(np.int16)).mean(axis=2)
    x = rectangle["x"]
    y = rectangle["y"]
    width = rectangle["width"]
    height = rectangle["height"]
    inside = difference[y : y + height, x : x + width]
    outside_mask = np.ones(difference.shape, dtype=bool)
    outside_mask[y : y + height, x : x + width] = False
    outside = difference[outside_mask]
    return {
        "cueRegionMeanAbsoluteChannelDifference": round(float(inside.mean()), 6),
        "outsideCueRegionMeanAbsoluteChannelDifference": round(float(outside.mean()), 6),
    }


def safe_capture_path(capture_directory: Path, file_name: str) -> Path:
    if Path(file_name).name != file_name or not file_name.endswith(".png"):
        raise ValueError("WebView2 report contains an unsafe capture file name.")
    path = capture_directory / file_name
    if not path.is_file():
        raise FileNotFoundError("A WebView2 capture is missing.")
    return path


def analyze(arguments: argparse.Namespace) -> int:
    manifest = read_json_object(arguments.manifest)
    validation = validate_manifest_object(manifest)
    webview_report = read_json_object(arguments.webview_report)
    width = int(manifest["profile"]["width"])
    height = int(manifest["profile"]["height"])
    if (
        webview_report.get("schemaVersion") != "1.0.0"
        or webview_report.get("actualWebView2Executed") is not True
        or webview_report.get("captureApi") != "CoreWebView2.CapturePreviewAsync"
        or webview_report.get("runtimeVersion") != webview_report.get("environmentBrowserVersion")
        or webview_report.get("externalNavigationAllowed") is not False
        or webview_report.get("dynamicDataSentViaPostWebMessageAsJson") is not True
        or webview_report.get("sourcePixelsTransferredToLocalWebView2") is not True
        or webview_report.get("browserProcessExitObservedBeforeHostResult") is not True
        or webview_report.get("userDataDirectoryRemoved") is not True
        or webview_report.get("sourcePixelsIncludedInResult") is not False
    ):
        raise ValueError("WebView2 host evidence is incomplete or inconsistent.")

    expected_rectangles = validation["rectangles"]
    captures = webview_report["captures"]
    if not isinstance(captures, list) or len(captures) != len(EXPECTED_MODES) * len(EXPECTED_SAMPLE_IDS):
        raise ValueError("WebView2 did not produce the complete capture matrix.")
    capture_directory = arguments.webview_report.parent
    capture_index: dict[tuple[str, str], tuple[dict[str, Any], Path, np.ndarray]] = {}
    for capture in captures:
        key = (str(capture["mode"]), str(capture["sampleId"]))
        if key in capture_index or key[0] not in EXPECTED_MODES or key[1] not in EXPECTED_SAMPLE_IDS:
            raise ValueError("WebView2 capture identifiers are duplicate or unexpected.")
        sample = next(item for item in manifest["samples"] if item["sampleId"] == key[1])
        capture_path = safe_capture_path(capture_directory, capture["capture"]["fileName"])
        image = load_image(capture_path, width, height)
        if (
            capture["cueExpected"] is not sample["cueExpected"]
            or capture["cueVisible"] is not sample["cueExpected"]
            or capture["rectangles"] != expected_rectangles
            or capture["requestedFontsAvailable"]["topic"] is not True
            or capture["requestedFontsAvailable"]["cue"] is not True
            or int(capture["capture"]["width"]) != width
            or int(capture["capture"]["height"]) != height
            or capture["capture"]["sha256"] != file_sha256(capture_path)
            or int(capture["capture"]["byteSize"]) != capture_path.stat().st_size
            or capture["sourcePixelsIncludedInHostResult"] is not False
        ):
            raise ValueError("A WebView2 capture contradicts the shared contract or local file.")
        capture_index[key] = (capture, capture_path, image)
    if set(capture_index) != {(mode, sample_id) for mode in EXPECTED_MODES for sample_id in EXPECTED_SAMPLE_IDS}:
        raise ValueError("WebView2 capture matrix is incomplete.")

    base_image = load_image(arguments.base_image, width, height)
    frame_times = observed_frame_times(arguments.ffprobe, arguments.faithful_video)
    faithful_images: dict[str, np.ndarray] = {}
    faithful_frames: list[dict[str, Any]] = []
    timing_errors: list[float] = []
    for sample in manifest["samples"]:
        requested = float(rational(sample["outputTime"]))
        frame_index = bisect.bisect_left(frame_times, requested)
        if frame_index >= len(frame_times):
            raise ValueError("No faithful-render frame exists at or after a requested sample.")
        observed = frame_times[frame_index]
        destination = arguments.work_directory / f"faithful-{sample['sampleId']}.png"
        image = extract_frame_by_index(
            arguments.ffmpeg,
            arguments.faithful_video,
            frame_index,
            width,
            height,
            destination,
        )
        faithful_images[sample["sampleId"]] = image
        timing_error = observed - requested
        timing_errors.append(timing_error)
        faithful_frames.append(
            {
                "sampleId": sample["sampleId"],
                "requestedOutputSeconds": round(requested, 9),
                "observedOutputSeconds": round(observed, 9),
                "firstObservedFrameNotEarlierThanRequested": timing_error >= -1e-9,
                "selectionDifferenceSeconds": round(timing_error, 9),
                "artifact": file_descriptor(destination),
            }
        )

    comparisons: list[dict[str, Any]] = []
    for mode in EXPECTED_MODES:
        for sample in manifest["samples"]:
            sample_id = sample["sampleId"]
            capture, capture_path, web_image = capture_index[(mode, sample_id)]
            faithful_image = faithful_images[sample_id]
            comparisons.append(
                {
                    "mode": mode,
                    "sampleId": sample_id,
                    "cueExpected": sample["cueExpected"],
                    "webView2CueVisible": capture["cueVisible"],
                    "sharedLayoutRectanglesMatch": capture["rectangles"] == expected_rectangles,
                    "webView2Capture": file_descriptor(capture_path),
                    "faithfulFrame": next(item["artifact"] for item in faithful_frames if item["sampleId"] == sample_id),
                    "webView2VersusFaithful": difference_metrics(web_image, faithful_image),
                    "webView2VersusBase": difference_metrics(web_image, base_image),
                    "faithfulVersusBase": difference_metrics(faithful_image, base_image),
                }
            )

    canvas_svg: list[dict[str, Any]] = []
    for sample in manifest["samples"]:
        sample_id = sample["sampleId"]
        canvas_image = capture_index[("canvas", sample_id)][2]
        svg_image = capture_index[("svg", sample_id)][2]
        canvas_svg.append(
            {
                "sampleId": sample_id,
                "cueExpected": sample["cueExpected"],
                "canvasVersusSvg": difference_metrics(canvas_image, svg_image),
            }
        )

    cue_box = expected_rectangles["cueBox"]
    cue_controls: list[dict[str, Any]] = []
    renderer_images: dict[str, dict[str, np.ndarray]] = {
        "webview2-canvas": {sample_id: capture_index[("canvas", sample_id)][2] for sample_id in EXPECTED_SAMPLE_IDS},
        "webview2-svg": {sample_id: capture_index[("svg", sample_id)][2] for sample_id in EXPECTED_SAMPLE_IDS},
        "ffmpeg-faithful": faithful_images,
    }
    for renderer, images in renderer_images.items():
        comparisons_to_controls: list[dict[str, Any]] = []
        for control_id in ("inactive-before", "inactive-after"):
            region = region_mean_difference(images["active-middle"], images[control_id], cue_box)
            comparisons_to_controls.append(
                {
                    "inactiveControlSampleId": control_id,
                    **region,
                    "cueRegionDifferenceExceedsOutsideDifference":
                        region["cueRegionMeanAbsoluteChannelDifference"]
                        > region["outsideCueRegionMeanAbsoluteChannelDifference"],
                }
            )
        inactive_metrics = difference_metrics(images["inactive-before"], images["inactive-after"])
        cue_controls.append(
            {
                "renderer": renderer,
                "activeSampleId": "active-middle",
                "inactiveComparisons": comparisons_to_controls,
                "bothInactiveSamples": inactive_metrics,
                "allCueRegionDifferencesExceedOutsideDifferences": all(
                    item["cueRegionDifferenceExceedsOutsideDifference"] for item in comparisons_to_controls
                ),
                "relativeDifferenceIsMechanismEvidenceNotProductionThreshold": True,
            }
        )

    report = {
        "status": "succeeded",
        "manifestValidation": validation,
        "baseFrame": file_descriptor(arguments.base_image),
        "faithfulInterval": {
            "artifact": file_descriptor(arguments.faithful_video),
            "observedFrameCount": len(frame_times),
            "firstObservedFrameSeconds": round(frame_times[0], 9),
            "lastObservedFrameSeconds": round(frame_times[-1], 9),
            "sampledFrames": faithful_frames,
            "sampleSelectionDifferenceSeconds": {
                "count": len(timing_errors),
                "minimum": round(min(timing_errors), 9),
                "median": round(statistics.median(timing_errors), 9),
                "maximum": round(max(timing_errors), 9),
            },
        },
        "webView2": {
            "actualWebView2Executed": True,
            "captureApi": webview_report["captureApi"],
            "runtimeVersion": webview_report["runtimeVersion"],
            "environmentBrowserVersion": webview_report["environmentBrowserVersion"],
            "sdkAssemblyVersion": webview_report["sdkAssemblyVersion"],
            "hostDpiScale": webview_report["hostDpiScale"],
            "zoomFactor": webview_report["zoomFactor"],
            "captureCount": len(captures),
            "cssViewports": [capture["viewport"] for capture in captures],
            "devicePixelRatios": [capture["devicePixelRatio"] for capture in captures],
            "allCaptureDimensionsMatchProfile": all(
                capture["capture"]["width"] == width and capture["capture"]["height"] == height
                for capture in captures
            ),
            "allRequestedFontsAvailable": all(
                capture["requestedFontsAvailable"]["topic"] is True
                and capture["requestedFontsAvailable"]["cue"] is True
                for capture in captures
            ),
            "allReportedRectanglesMatchSharedSpec": all(
                capture["rectangles"] == expected_rectangles for capture in captures
            ),
            "allCueStatesMatchSharedCue": all(
                capture["cueVisible"] is capture["cueExpected"] for capture in captures
            ),
            "externalNavigationAllowed": False,
            "dynamicDataSentViaPostWebMessageAsJson": True,
            "sourcePixelsTransferredToLocalWebView2": True,
            "externalSubresourceAndConnectAllowedByDocument": False,
            "browserProcessExitObservedBeforeHostResult": True,
            "userDataDirectoryRemoved": True,
        },
        "comparisons": comparisons,
        "canvasVersusSvg": canvas_svg,
        "cueMechanismControls": cue_controls,
        "summary": {
            "sharedLayoutSpecAndCueConsumedByAllRenderers": True,
            "comparisonCount": len(comparisons),
            "canvasSvgComparisonCount": len(canvas_svg),
            "allWebView2GeometryReportsMatchSharedSpec": all(
                item["sharedLayoutRectanglesMatch"] for item in comparisons
            ),
            "allWebView2CueStatesMatchSharedCue": all(
                item["cueExpected"] is item["webView2CueVisible"] for item in comparisons
            ),
            "allRendererCueControlsSeparateFromOutsideRegion": all(
                item["allCueRegionDifferencesExceedOutsideDifferences"] for item in cue_controls
            ),
            "webView2VersusFaithfulDifferenceRecorded": len(comparisons) == 6,
            "canvasVersusSvgDifferenceRecorded": len(canvas_svg) == 3,
            "exactPixelEqualityRequired": False,
            "perceptualQualityMeasured": False,
            "movingVideoPlaybackMeasured": False,
            "audioPreviewMeasured": False,
            "crossDpiScaleReproducibilityMeasured": False,
            "crossRuntimeGpuDriverReproducibilityMeasured": False,
            "productionThresholdsApproved": False,
        },
        "privacy": {
            "sourcePixelsIncludedInReport": False,
            "sourceAudioIncludedInReport": False,
            "sourceTranscriptTextIncludedInReport": False,
            "neutralCueTextIncludedInReport": False,
            "sourceDerivedArtifactsRemainLocal": True,
            "sourcePixelsTransferredOnlyToLocalWebView2": True,
            "absolutePathsIncludedInReport": False,
        },
    }
    print(json.dumps(report))
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)

    validate = subparsers.add_parser("validate-manifest")
    validate.add_argument("--manifest", required=True, type=Path)
    validate.set_defaults(handler=validate_manifest)

    comparison = subparsers.add_parser("analyze")
    comparison.add_argument("--manifest", required=True, type=Path)
    comparison.add_argument("--base-image", required=True, type=Path)
    comparison.add_argument("--faithful-video", required=True, type=Path)
    comparison.add_argument("--webview-report", required=True, type=Path)
    comparison.add_argument("--ffmpeg", required=True, type=Path)
    comparison.add_argument("--ffprobe", required=True, type=Path)
    comparison.add_argument("--work-directory", required=True, type=Path)
    comparison.set_defaults(handler=analyze)
    return parser


def main() -> int:
    arguments = build_parser().parse_args()
    return int(arguments.handler(arguments))


if __name__ == "__main__":
    sys.exit(main())