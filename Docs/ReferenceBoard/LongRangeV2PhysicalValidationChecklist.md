# Long-Range V2 Physical Validation

Use this repeatable session for the print-tolerant AprilTag-only board. It is a
measurement plan only; do not change board geometry or compensate print error in software.

## Candidate Geometry

| Property | Value |
| --- | --- |
| Board | 360 x 360 mm |
| Printed tile | 170 mm square |
| Detector quadrilateral | 94.444 mm square |
| Marker centers | (+/-90, +/-90) mm |
| Marker spacing | 180 mm |
| Outer margin | 5 mm |
| Center gap | 10 mm |
| ARCore image score | 100 |

## A. Print Verification

Before a camera test, print at **Actual Size / 100%**. Disable **Fit**,
**Shrink**, **Scale to Fit**, and printer poster scaling. The supplied tiled PDF
already contains the required 1:1 tiling.

Record the following with a steel ruler or caliper:

| Check | Target | Acceptable tolerance |
| --- | --- | --- |
| PDF calibration line | 100.0 mm | +/-0.5 mm |
| Assembled board width | 360.0 mm | +/-1.0 mm |
| Assembled board height | 360.0 mm | +/-1.0 mm |
| Horizontal marker-center spacing | 180.0 mm | +/-1.0 mm |
| Vertical marker-center spacing | 180.0 mm | +/-1.0 mm |
| Outer margin | 5.0 mm | +/-0.5 mm |
| Center gap | 10.0 mm | +/-0.5 mm |

If any check is outside tolerance, stop the camera test and correct the print,
tiling, trim, or assembly. Never compensate a physical measurement discrepancy
with runtime offsets, tag sizes, or board-center changes.

## B. DJI Long-Range Test

Use the AprilTag-only board, a stable board mount, and a safe clear flight area.
At approximately 2 m, 5 m, 10 m, 15 m, and 20 m, hold the camera steady for at
least 10 seconds. Continue only when safe and the board remains practical to see.

For each distance record: processed frames; frames with at least one marker;
marker-count distribution; accepted PnP count; reprojection RMS; maximum corner
residual; detector processing latency; and frame-to-Unity latency. Also mark the
furthest distance with reliable 4-marker, >=2-marker, >=1-marker, and accepted
board-pose operation.

Capture rate-limited logcat entries for `DJI_VISION_FRAME_ACCEPTED`,
`DJI_BOARD_MARKERS_VISIBLE`, `DJI_BOARD_RESULT_ACCEPTED`,
`DJI_VISION_TIMESTAMP`, `DJI_BOARD_REPROJECTION_RMS`, and
`DJI_BOARD_MAX_CORNER_ERROR`. Preserve the raw logcat file for every distance.

## C. DJI Angle Test

At a distance with stable four-marker recognition, hold each pose for 10 seconds:
frontal, moderate yaw, moderate pitch, and a stronger oblique view. Record marker
count, accepted-pose rate, RMS, worst residual, latency, and the first visible
degradation. Do not change calibration or PnP thresholds during the session.

## D. Phone Full-Board ARCore Test

Set `PhoneReferenceMode = FullAprilTagBoard`; retain `BuildingReference` as the
separate later comparison path. Record first-detection time and approximate
distance, stable tracking distance, oblique-angle behavior, partial board
visibility, loss, reacquisition, and cube stability while the phone moves.

Save logs containing `PHONE_REFERENCE_DETECTED`, `PHONE_BOARD_POSE`,
`PHONE_REFERENCE_TRACKING_CHANGED`, and `distanceCameraToBoardM`. Repeat the
same protocol later with `PhoneReferenceMode = BuildingReference`; do not remove
either reference path before that comparison.

## E. DJI Propagation Transition

After a high-confidence visual pose, move the camera so the fixed board is out
of view. Verify `DJI_RENDER_POSE_SOURCE=TELEMETRY_PROPAGATED` at the first
propagated pose. Return to the board within 0.5-1.5 seconds and verify that
`VISUAL` returns after the quality, 250 ms stability and matched rotation-only
pose checks. Repeat with the board out of view past 2 seconds: valid telemetry
must keep the same render source while `telemetryPropagationPhase` becomes
`LONG_LIVED`. Visual return then requires quality and stability but not the
short-window pose-distance check. Separately, verify `NONE` and model hiding
when no usable visual pose or valid propagation is available.

## Log Collection

Use a new log file per test leg, for example:

```powershell
adb logcat -c
adb logcat -v threadtime | Tee-Object "long-range-v2-<leg>.log"
```

Write distance, angle, lighting, weather, board mount, camera resolution, and
the exact build commit at the top of each log file.

## Final Report Template

```text
LONG-RANGE V2 PHYSICAL VALIDATION

print scale =
board measured size =

DJI:
max reliable distance =
4-marker distance =
2-marker distance =
1-marker distance =
RMS median/p95 =
latency median/p95 =

PHONE:
full-board detected =
first-detection distance =
tracking stability =
reacquisition =

PROPAGATION:
VISUAL -> TELEMETRY_PROPAGATED =
TELEMETRY_PROPAGATED -> VISUAL (short) =
TELEMETRY_PROPAGATED remains active after 2 s (when valid) =
TELEMETRY_PROPAGATED -> VISUAL (long) =
NONE when propagation cannot continue =

FINAL:
board geometry accepted =
phone BuildingReference still needed =
```
