"""Inspect DJI DNG metadata relevant to a later calibration audit.

This utility does not infer OpenCV coefficients or overwrite a runtime
calibration. It exports the DNG metadata that can validate model, image crop,
focal length, optical-center and lens-warp provenance when a Mini 3 Pro DNG is
available. It requires the external ExifTool executable on PATH.
"""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
from pathlib import Path


INTERESTING_TAGS = {
    "Make", "Model", "UniqueCameraModel", "CameraModelName", "FocalLength",
    "FocalLengthIn35mmFormat", "ImageWidth", "ImageHeight", "ExifImageWidth",
    "ExifImageHeight", "ActiveArea", "DefaultCropOrigin", "DefaultCropSize",
    "DefaultUserCrop", "DefaultScale", "OpcodeList1", "OpcodeList2", "OpcodeList3",
    "WarpRectilinear", "WarpFisheye", "DewarpData", "CalibratedOpticalCenterX",
    "CalibratedOpticalCenterY", "ColorMatrix1", "AsShotNeutral",
}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("dng", type=Path, help="Original DJI .DNG file; do not pre-process it")
    parser.add_argument("--output", type=Path, help="Optional JSON report path")
    return parser.parse_args()


def find_tags(metadata: dict) -> dict:
    result: dict[str, object] = {}
    for key, value in metadata.items():
        bare_key = key.rsplit(":", 1)[-1]
        if bare_key in INTERESTING_TAGS:
            result[bare_key] = value
    return result


def main() -> int:
    args = parse_args()
    if not args.dng.is_file():
        print(f"DNG not found: {args.dng}", file=sys.stderr)
        return 2
    if shutil.which("exiftool") is None:
        print("ExifTool is required. Install it and make exiftool available on PATH.", file=sys.stderr)
        return 3

    command = ["exiftool", "-j", "-n", "-G1", "-s", str(args.dng)]
    completed = subprocess.run(command, check=False, capture_output=True, text=True)
    if completed.returncode != 0:
        print(completed.stderr, file=sys.stderr)
        return completed.returncode or 4
    records = json.loads(completed.stdout)
    metadata = records[0] if records else {}
    report = {
        "sourceFile": str(args.dng),
        "metadata": find_tags(metadata),
        "interpretation": {
            "runtimeCalibrationChanged": False,
            "note": "Inspect any DNG warp/optical-center fields against the exact 1920x1080 ImageReader path before attempting a model conversion.",
        },
    }
    rendered = json.dumps(report, indent=2, sort_keys=True)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(rendered, encoding="utf-8")
    print(rendered)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
