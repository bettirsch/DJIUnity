# DJI CPU Camera Calibration

This process calibrates **only** the raw luma frame that
`DjiBoardVisionBridge` passes to the native board detector. It does not use a
Unity OES texture, screen capture, crop, resize, rotation, mirror, or RGB
conversion.

## Target

`DjiCalibrationCheckerboard_A4_25mm.pdf` is an A4 landscape checkerboard:

- 9 x 6 inner corners;
- 10 x 7 printed squares;
- 25 mm square side;
- 250 mm x 175 mm active checkerboard area.

Print it at **100% / actual size** with fit-to-page disabled. Check several
printed squares with a ruler before capture.

## Capture

1. Open DroneView and wait for `DJI_CALIBRATION_FRAME` in Android logcat. It
   reports the exact delivered detector geometry and states that the input is
   unmodified `YUV_420_888_LUMA8`.
2. Trigger a bounded capture from a temporary Unity diagnostic control or C#:

   ```csharp
   FindFirstObjectByType<DjiBoardVisionProvider>()?.RequestCalibrationCapture(30);
   ```

3. The Android log reports the app-private directory and every saved `.pgm`
   raw luma frame. Capture 20-40 frames: center, all corners, all edges,
   several distances, roll angles, and moderate perspective views. Keep the
   checkerboard sharp; do not use only centered front-facing frames.
4. Pull the entire capture directory, including `capture_manifest.jsonl`.

Each saved PGM has exactly the geometry passed to AprilTag detection. It is
not a display screenshot and must not be altered before calibration.

## Offline Solve

Install the offline dependency once in the environment used for calibration:

```powershell
python -m pip install -r Tools/dji-calibration-requirements.txt
```

Then solve and generate the runtime file:

```powershell
python Tools/CalibrateDjiCpuFrames.py `
  --input C:\captures\session_123 `
  --output Assets\Resources\DjiCameraCalibration.json `
  --report Docs\DjiCameraCalibration\latest-calibration-report.json
```

The report retains every usable frame's reprojection error and documents any
single-pass high-error exclusions. The solver fails rather than hiding a poor
data set by excluding more than 25% of observations.

`DjiCameraCalibration.json` becomes valid only when its frame width, height,
and `YUV_420_888_LUMA8` format exactly match the runtime detector frame.
