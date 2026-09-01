# DJI Reference Board Localization

The physical board frame is shared by phone AR tracking and DJI board vision:

- origin: center of the 360 mm x 360 mm printed board;
- `+X`: right across the print;
- `+Y`: upward across the print;
- `+Z`: outward from the printed surface.

The existing `BuildingReference` image stays centered and remains 180 mm x
180 mm. The DJI-only layout adds four `tagStandard41h12` AprilTags, IDs 0-3,
at `(-135,+135)`, `(+135,+135)`, `(-135,-135)`, `(+135,-135)` mm in board
coordinates. Each tag is a 60 mm square with its printed top aligned to board
`+Y` and its front normal aligned to board `+Z`.

`Docs/ReferenceBoard/DjiReferenceBoard_360mm.pdf` is the generated physical
artifact. It must be printed at 100% scale; its README contains the required
post-print dimensional checks.

The Android bridge receives a second decoded camera output in an
`ImageReader` YUV_420_888 surface. This CPU path is independent of, and does
not read from, the Unity OES texture used for video display. The native module
uses the official AprilRobotics AprilTag library, detects all configured tags,
and performs one robust reprojection refinement across all visible marker
corners. It does not average individual marker rotations.

`DjiCameraCalibration.json` must be replaced with intrinsics calibrated for
the exact ImageReader frame mode before world-pose initialization is enabled.
The committed file intentionally uses zero intrinsics and is marked
`provisional`; that configuration permits marker diagnostics only. CPU frames
are currently expected to be uncropped, unrotated, and unmirrored 1920x1080.
If the decoder output differs, update the calibration and frame-transform
handling together rather than changing pose axes heuristically.

The transform convention is parent-from-child. Once a quality-gated visual
measurement provides `T_camera_board`, DJI visual localization computes:

```text
T_world_camera = T_world_board * inverse(T_camera_board)
```

No DJI telemetry is fused into this visual pose in this stage. A temporary
loss of board visibility preserves the last accepted visual pose; propagation
and relocalization corrections are later work.
