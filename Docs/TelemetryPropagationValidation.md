# DJI Telemetry Propagation Validation

## Current capability

Render authority is `VISUAL`, `TELEMETRY_PROPAGATED`, or `NONE`. When a
usable visual pose is lost and a valid propagation prediction is available,
`TELEMETRY_PROPAGATED` starts immediately. The monotonic outage timer starts
on the first rendered propagation pose; neither a rejected visual candidate
nor crossing the short-window boundary restarts it. The board/world and model
placements remain fixed; the estimated camera pose can use aircraft/gimbal
orientation, admitted horizontal GNSS and fresh altitude in **both** phases.
Missing or stale position channels retain their last contribution.

| Elapsed propagation time | Continuation | Return to visual |
| --- | --- | --- |
| Through 2 s, inclusive | Existing valid prediction and enabled propagation | High-confidence visual pose and 250 ms stability, plus agreement within 1 m and 6 degrees of the rotation-only prediction using telemetry interpolated to the visual frame timestamp |
| After 2 s | Active prediction and baseline, with valid telemetry orientation | Same visual quality and 250 ms stability, without the short-window pose-distance check |

There is no render-source change at 2 s. If propagation cannot continue and
there is no eligible visual pose, `NONE` applies. In particular, an explicitly
invalid orientation clears the propagation state; stale GNSS or altitude
alone does not hide the model.

For new captures, `board.renderPoseSource` is `TelemetryPropagated` in both
phases and `board.telemetryPropagationPhase` is `SHORT_WINDOW` or
`LONG_LIVED` when the timer is available. `enteredTelemetryPropagated`
now indicates propagated authority, not crossing the two-second boundary.
When comparing old logs, `RotationPropagated` / `ROTATION_PROPAGATED` denotes
the old short window; `TelemetryPropagated` in those logs denotes the old long
window. Both are telemetry authority. The old short-to-long transition is
historical phase evidence, not a transition to expect from new builds.

## Historical Rotation-Only Validation

`SIGNED_YAW_VALIDATION = PASS`

The controlled physical signed-yaw test used the twist of each `C1FromC0`
quaternion around physical NED `+Z` (down), expressed in the current camera
frame. Positive is right-hand rotation about NED down, clockwise viewed from
above. It does not compare separate Unity/DJI Euler-yaw values.

Validated legs, all with `directionMismatches = 0` and
`gimbalPitchChangeDeg = 0`:

- `CENTER_TO_RIGHT`: visual `+21.36 deg`, telemetry `+20.57 deg`.
- `RIGHT_TO_CENTER`: visual `-20.11 deg`, telemetry `-20.22 deg`.
- `CENTER_TO_LEFT`: visual `-10.06 deg`, telemetry `-9.21 deg`.
- `LEFT_TO_CENTER`: visual `+9.27 deg`, telemetry `+9.59 deg`.

`GIMBAL_PITCH_VALIDATION = PASS`

`FULL_3D_ROTATION_AGREEMENT = GOOD`

Short-horizon full-3D rotation-error p95 values were `0.32 deg` at 250 ms,
`0.76 deg` at 1 second, `0.97 deg` at 2 seconds, and `1.28 deg` at 5 seconds.
These measurements validated the original rotation-only prediction. The two
second boundary now determines continuation and reacquisition policy, not a
fixed limit on persistent-world rendering.

The older `MIXED_MOTION_INCONCLUSIVE` classification applies only to the
historical mixed yaw/pitch diagnostic interval and does not override these
clean controlled validations.

The historical rotation-only predictor assumed zero camera translation.
This remains true for the short-window pose comparison, not for the current
persistent-world render estimate.

## Completed Physical Test - DJI Yaw

The following procedure produced the signed-yaw validation above:

- Use normal ambient lighting and preferably the printed physical board.
- Keep the entire board visible and gimbal pitch approximately fixed.
- Move in a controlled sequence: center, `+10..15 deg` yaw, center,
  `-10..15 deg` yaw, center.
- The diagnostic derives signed yaw from the twist of each `C1FromC0`
  quaternion around physical NED `+Z` (down), expressed in the current camera
  frame. Positive is right-hand rotation about NED down, clockwise viewed from
  above. It does not compare separate Unity/DJI Euler-yaw values.
- Complete all four automatically logged legs: `CENTER_TO_LEFT`,
  `LEFT_TO_CENTER`, `CENTER_TO_RIGHT`, and `RIGHT_TO_CENTER`.
- A leg is rejected as `CONTAMINATED_BY_GIMBAL_MOTION` when its gimbal pitch
  changes by more than 2 degrees; it also requires sufficient paired samples.

The earlier dark-room, bright-monitor test produced possible signed-yaw
inconsistencies. It remains diagnostic history only and must not drive camera
orientation changes or override the controlled physical result.
