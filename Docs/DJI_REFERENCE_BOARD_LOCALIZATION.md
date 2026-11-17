# DJI Reference Board Localization

The physical board defines the frame used by DJI board vision:

- origin: center of the 360 mm x 360 mm printed board;
- `+X`: right across the print;
- `+Y`: upward across the print;
- `+Z`: outward from the printed surface.

The maximized LONG-RANGE V2 board is AprilTag-only: it has no central
`BuildingReference` artwork. It uses four `tagStandard41h12` AprilTags, IDs
0-3, at `(-90,+90)`, `(+90,+90)`, `(-90,-90)`, `(+90,-90)` mm in board
coordinates. Each full printed tile is 170 mm with a 5 mm outer board margin
and a 10 mm center gap; the physical pose side supplied to the native estimator
is its 94.444 mm detector-corner quadrilateral.

The native AprilTag pose frame has `+X` print-right, `+Y` print-down, and `+Z`
into the board. `T_board_marker` is the proper `R_x(180 degrees)` rotation:
marker `+X -> board +X`, marker `+Y -> board -Y`, and marker `+Z -> board -Z`.
The board frame remains `+X` print-right, `+Y` print-up, `+Z` outward.

`Docs/ReferenceBoard/DjiReferenceBoard_LongRangeV2_AprilTagsOnly_360mm.pdf`
is the generated physical artifact. It must be printed at 100% scale; its
README contains the required post-print dimensional checks.

The Android MSDK `CameraFrameListener` receives NV21 and copies only the
packed Y plane for the detector (`NV21_LUMA8`). This CPU path is separate
from the Unity OES texture used for video display. The native module
uses the official AprilRobotics AprilTag library, detects all configured tags,
and performs one robust reprojection refinement across all visible marker
corners. It does not average individual marker rotations.

The project uses a single approximate DJI Mini 3 Pro / FC3582 camera model:
1920 x 1080, `fx=fy=1332.70 px`, `cx=960 px`, `cy=540 px`, zero distortion.
The same intrinsics are used for native pose estimation and Unity projection.
The focal estimate comes from the Lensfun 6.7 mm nominal focal length and
DJI's 1.197 um pixel pitch, scaled from the assumed 8064 px sensor width to
1920 px. The principal point assumes a centered crop in the DJI video
pipeline. The detector itself applies no additional crop, resize, rotation,
or mirror. Unity rejects frames with a different format or pixel geometry.
These are prototype intrinsics, not physical measurements or a guarantee of
absolute 3D accuracy.

The transform convention is parent-from-child. Once a quality-gated visual
measurement provides `T_camera_board`, DJI visual localization computes:

```text
T_world_camera = T_world_board * inverse(T_camera_board)
```

The first authoritative visual pose establishes the persistent world with
`T_world_board = I`. The selected Cube or RoofCanopy stays fixed in this
frame. If the visual pose becomes unusable, eligible DJI telemetry can
propagate the camera pose; a new accepted visual pose restores visual
authority. The detector and render paths use the same initial acquisition
and visual quality requirements as before.
