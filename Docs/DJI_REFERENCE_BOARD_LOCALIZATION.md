# DJI Reference Board Localization

The physical board frame is shared by phone AR tracking and DJI board vision:

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

The Android bridge receives a second decoded camera output in an
`ImageReader` YUV_420_888 surface. This CPU path is independent of, and does
not read from, the Unity OES texture used for video display. The native module
uses the official AprilRobotics AprilTag library, detects all configured tags,
and performs one robust reprojection refinement across all visible marker
corners. It does not average individual marker rotations.

`DjiCameraCalibration.json` must be generated from raw ImageReader luma frames
before world-pose initialization is enabled. The committed file intentionally
uses zero intrinsics and is marked `provisional`; that configuration permits
marker diagnostics only. The full capture, printable checkerboard, offline
solve, frame-level errors, and runtime geometry checks are documented in
`Docs/DjiCameraCalibration/README.md`. CPU frames are currently expected to be
uncropped, unrotated, and unmirrored 1920x1080. If the delivered frame differs,
calibrate the exact new geometry instead of changing pose axes heuristically.

The transform convention is parent-from-child. Once a quality-gated visual
measurement provides `T_camera_board`, DJI visual localization computes:

```text
T_world_camera = T_world_board * inverse(T_camera_board)
```

No DJI telemetry is fused into this visual pose in this stage. A temporary
loss of board visibility preserves the last accepted visual pose; propagation
and relocalization corrections are later work.
