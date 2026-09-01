# Public FC3582 Provisional Profile

`Assets/Resources/DjiCameraCalibrationProvisionalFc3582.json` is an
**experimental** Mini 3 Pro / FC3582 profile for the actual DJI board-detector
frame. It is never a measured calibration and cannot initialize
`DJI_WORLD_INITIALIZED` unless `allowProvisionalCalibrationForTesting` is
explicitly enabled. A valid measured `DjiCameraCalibration.json` always wins.

## Detector frame

The bridge creates a dedicated `ImageReader` at 1920 x 1080 in
`YUV_420_888`, copies only plane 0 to a packed `YUV_420_888_LUMA8` buffer, and
passes that buffer directly to native detection. There is no crop, resize,
rotation, mirror, or YUV-to-RGB conversion. On-device `DJI_RUNTIME_FRAME`
logs the delivered geometry and must confirm this before evaluating the
profile.

## Parameters and confidence

| Parameter | Value | Origin and confidence |
| --- | --- | --- |
| Camera model | Mini 3 Pro / FC3582 | Lensfun FC3582 entry; medium |
| Sensor | 1/1.3-inch CMOS, 48 MP | DJI specification; high |
| Full still image | 8064 x 6048 | DJI specification; high |
| Sensor pixel pitch | 1.197 um | DJI specification; high |
| Nominal focal length | 6.7 mm | Lensfun FC3582 entry; medium |
| Crop factor | 3.6 | Lensfun FC3582 entry; medium |
| 35 mm equivalent | 24 mm | DJI specification; high |
| DJI FOV | 82.1 degrees, direction not labelled | DJI specification; high value, low directional certainty |
| Detector geometry | 1920 x 1080 packed luma | project bridge path; must be verified on device |
| `fx`, `fy` | 1332.70 px | inferred; medium-to-low until physical test |
| `cx`, `cy` | 960, 540 px | centered-crop assumption; low-to-medium |

The focal estimate is derived without treating DJI's unlabeled FOV as a
horizontal FOV:

```text
sensorFx = 6.7 mm / 0.001197 mm per pixel = 5597.33 px
detectorFx = sensorFx * 1920 / 8064 = 1332.70 px
```

The calculation assumes the 16:9 video path retains the full 8064-pixel
sensor width, uses a centered vertical crop, and is then scaled to 1920 x
1080. It consequently produces an approximately 82.1-degree **diagonal**
FOV, which is a consistency check only: DJI's camera spec does not identify
the published FOV as diagonal, horizontal, or vertical.

## Distortion

Lensfun reports a `ptlens` distortion model at 6.7 mm with
`a=-0.0075`, `b=0.026`, and `c=-0.0027`. PTLens is a separately defined radial
model and does not share OpenCV Brown-Conrady's normalized
`k1,k2,p1,p2,k3` parameterization. The profile therefore deliberately sends
zero OpenCV distortion rather than fabricating a conversion. The checkerboard
solve is the path that produces compatible Brown-Conrady coefficients.

## Public sources

- DJI Mini 3 Pro support specifications: <https://www.dji.com/support/product/mini-3-pro>
- Lensfun FC3582 camera/lens and PTLens coefficients: <https://github.com/lensfun/lensfun/blob/master/data/db/actioncams.xml>
- Lensfun model documentation: <https://github.com/lensfun/lensfun/blob/master/docs/manual-main.txt>

## Evaluation protocol

1. Build and run a fresh Android player and confirm `DJI_RUNTIME_FRAME` is
   exactly 1920 x 1080, unmodified packed luma.
2. Leave prototype mode disabled for normal operation. To run the controlled
   test, set `allowProvisionalCalibrationForTesting` on
   `DjiBoardVisionProvider` before launch.
3. Use the 360 mm board at center, left/right, upper/lower frame areas, varied
   distance and moderate tilt. Hold it still for at least ten seconds at each
   pose.
4. Retain the logs for `DJI_BOARD_REPROJECTION_RMS`,
   `DJI_BOARD_MAX_CORNER_ERROR`, `DJI_BOARD_POSE_JITTER_POSITION`, and
   `DJI_BOARD_POSE_JITTER_ROTATION`. The existing cyan/yellow diagnostic
   overlay shows projected and detected corners; RGB axes show the board pose.
5. Only a measured data set may establish
   `PUBLIC_CALIBRATION_RESULT = SUFFICIENT`. With no physical metrics this
   profile's current conclusion is:

```text
PUBLIC_CALIBRATION_RESULT = PHYSICAL_CALIBRATION_REQUIRED
```

The supplied checkerboard capture and offline solve remain the production
fallback; public values do not overwrite their output.
