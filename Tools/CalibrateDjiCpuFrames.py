"""Calibrate the raw DJI ImageReader luma frames used by board localization.

Requires OpenCV. The input frames are the PGM files written by
DjiBoardVisionBridge; do not substitute Unity screenshots or OES captures.
"""

from __future__ import annotations

import argparse
import json
import sys
from datetime import datetime, timezone
from pathlib import Path

import cv2
import numpy as np


INNER_CORNERS = (9, 6)
SQUARE_SIZE_METERS = 0.025
MINIMUM_VALID_IMAGES = 15
FRAME_FORMAT = "YUV_420_888_LUMA8"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, required=True, help="Directory containing captured .pgm frames")
    parser.add_argument("--output", type=Path, required=True, help="DjiCameraCalibration.json destination")
    parser.add_argument("--report", type=Path, required=True, help="Detailed JSON calibration report")
    return parser.parse_args()


def object_corners() -> np.ndarray:
    corners = np.zeros((INNER_CORNERS[0] * INNER_CORNERS[1], 3), np.float32)
    corners[:, :2] = np.mgrid[0 : INNER_CORNERS[0], 0 : INNER_CORNERS[1]].T.reshape(-1, 2)
    corners *= SQUARE_SIZE_METERS
    return corners


def find_corners(image: np.ndarray) -> np.ndarray | None:
    flags = cv2.CALIB_CB_EXHAUSTIVE | cv2.CALIB_CB_ACCURACY | cv2.CALIB_CB_NORMALIZE_IMAGE
    found, corners = cv2.findChessboardCornersSB(image, INNER_CORNERS, flags)
    if found:
        return corners.astype(np.float32)

    found, corners = cv2.findChessboardCorners(image, INNER_CORNERS)
    if not found:
        return None
    criteria = (cv2.TERM_CRITERIA_EPS | cv2.TERM_CRITERIA_MAX_ITER, 50, 1e-4)
    return cv2.cornerSubPix(image, corners, (11, 11), (-1, -1), criteria)


def per_image_errors(
    object_points: list[np.ndarray], image_points: list[np.ndarray], rvecs: list[np.ndarray], tvecs: list[np.ndarray], matrix: np.ndarray, distortion: np.ndarray
) -> list[float]:
    errors: list[float] = []
    for objects, observed, rvec, tvec in zip(object_points, image_points, rvecs, tvecs):
        projected, _ = cv2.projectPoints(objects, rvec, tvec, matrix, distortion)
        errors.append(float(cv2.norm(observed, projected, cv2.NORM_L2) / np.sqrt(len(projected))))
    return errors


def calibrate(image_records: list[dict], image_size: tuple[int, int]) -> tuple[float, np.ndarray, np.ndarray, list[np.ndarray], list[np.ndarray], list[float]]:
    objects = [record["object_points"] for record in image_records]
    points = [record["image_points"] for record in image_records]
    rms, matrix, distortion, rvecs, tvecs = cv2.calibrateCamera(objects, points, image_size, None, None)
    errors = per_image_errors(objects, points, rvecs, tvecs, matrix, distortion)
    return float(rms), matrix, distortion.reshape(-1), rvecs, tvecs, errors


def main() -> int:
    args = parse_args()
    frame_paths = sorted(args.input.glob("*.pgm")) + sorted(args.input.glob("*.png"))
    if not frame_paths:
        print("No .pgm or .png calibration frames found.", file=sys.stderr)
        return 2

    records: list[dict] = []
    rejected: list[dict] = []
    expected_size: tuple[int, int] | None = None
    template = object_corners()
    for frame_path in frame_paths:
        image = cv2.imread(str(frame_path), cv2.IMREAD_GRAYSCALE)
        if image is None:
            rejected.append({"file": frame_path.name, "reason": "UNREADABLE"})
            continue
        image_size = (int(image.shape[1]), int(image.shape[0]))
        if expected_size is None:
            expected_size = image_size
        if image_size != expected_size:
            rejected.append({"file": frame_path.name, "reason": f"GEOMETRY_MISMATCH expected={expected_size} actual={image_size}"})
            continue
        corners = find_corners(image)
        if corners is None:
            rejected.append({"file": frame_path.name, "reason": "CHECKERBOARD_NOT_FOUND"})
            continue
        records.append({"file": frame_path.name, "object_points": template.copy(), "image_points": corners})

    if expected_size is None or len(records) < MINIMUM_VALID_IMAGES:
        print(f"Need at least {MINIMUM_VALID_IMAGES} valid checkerboard frames; found {len(records)}.", file=sys.stderr)
        args.report.write_text(json.dumps({"status": "FAILED", "rejected": rejected}, indent=2), encoding="utf-8")
        return 3

    initial_rms, _, _, _, _, initial_errors = calibrate(records, expected_size)
    median = float(np.median(initial_errors))
    mad = float(np.median(np.abs(np.asarray(initial_errors) - median)))
    outlier_limit = median + max(0.5, 3.0 * 1.4826 * mad)
    candidate_indices = [index for index, error in enumerate(initial_errors) if error > outlier_limit]
    # One documented pass only. Refuse to mask a generally bad data set by
    # throwing away more than one quarter of the usable observations.
    if candidate_indices and len(candidate_indices) <= len(records) // 4:
        kept_records = [record for index, record in enumerate(records) if index not in candidate_indices]
        for index in candidate_indices:
            rejected.append({"file": records[index]["file"], "reason": "HIGH_REPROJECTION_ERROR", "error": initial_errors[index], "threshold": outlier_limit})
    else:
        kept_records = records

    if len(kept_records) < MINIMUM_VALID_IMAGES:
        print("Too few calibration images remain after documented outlier rejection.", file=sys.stderr)
        return 4

    rms, matrix, distortion, _, _, errors = calibrate(kept_records, expected_size)
    distortion = np.pad(distortion, (0, max(0, 5 - len(distortion))), constant_values=0.0)
    per_image = [{"file": record["file"], "reprojectionRms": error} for record, error in zip(kept_records, errors)]
    calibration = {
        "calibrationVersion": "DJI_CPU_CHECKERBOARD_V1",
        "imageWidth": expected_size[0],
        "imageHeight": expected_size[1],
        "detectorFrameFormat": FRAME_FORMAT,
        "pixelFormat": FRAME_FORMAT,
        "fx": float(matrix[0, 0]),
        "fy": float(matrix[1, 1]),
        "cx": float(matrix[0, 2]),
        "cy": float(matrix[1, 2]),
        "distortionCoefficients": [float(value) for value in distortion[:5]],
        "k1": float(distortion[0]), "k2": float(distortion[1]), "p1": float(distortion[2]), "p2": float(distortion[3]), "k3": float(distortion[4]),
        "calibrationRms": rms,
        "calibrationDate": datetime.now(timezone.utc).isoformat(),
        "valid": True,
        "rotationDegrees": 0,
        "mirrorX": False,
        "provisional": False,
        "source": f"CHECKERBOARD {INNER_CORNERS[0]}x{INNER_CORNERS[1]} inner corners, {SQUARE_SIZE_METERS:.3f} m squares; {len(kept_records)} frames",
    }
    report = {
        "status": "SUCCESS", "imageSize": expected_size, "checkerboardInnerCorners": INNER_CORNERS,
        "squareSizeMeters": SQUARE_SIZE_METERS, "initialCalibrationRms": initial_rms,
        "outlierThreshold": outlier_limit, "calibration": calibration, "perImage": per_image, "rejected": rejected,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(calibration, indent=2), encoding="utf-8")
    args.report.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
