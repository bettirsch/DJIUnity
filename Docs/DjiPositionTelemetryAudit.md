# DJI Position Telemetry Audit

## Scope

This is a diagnostic-only audit for the DJI Mini 3 Pro integration using the
locally imported `dji-sdk-v5-aircraft-provided-5.17.0.jar`. No telemetry value
is used for position propagation, local-NED conversion, or rendering.

## Existing Bridge Coverage

Before this audit, `DJIPoseBridge` listened only to:

- `DJIFlightControllerKey.KeyAircraftLocation`: `LocationCoordinate2D`
- `DJIFlightControllerKey.KeyAltitude`: `Double`
- aircraft/gimbal attitude and gimbal-relative yaw keys

The bridge did not subscribe to the 3D location, velocity, GNSS quality,
home-point, takeoff-relative, or height-quality candidates below.

## MSDK 5.17.0 Candidates

| Key | Value type | Local API evidence | Semantics available from local artifact |
| --- | --- | --- | --- |
| `KeyAircraftLocation` | `LocationCoordinate2D` | Present | Latitude/longitude; datum is not documented in the local artifact. |
| `KeyAircraftLocation3D` | `LocationCoordinate3D` | Present | Latitude, longitude, altitude; altitude reference is not documented locally. |
| `KeyAltitude` | `Double` | Present | Reference is not documented locally; not interchangeable with other height sources. |
| `KeyHeightAboveSeaLevel` | `HeightAboveSeaLevelMsg` | Present | `height` plus `AltitudeDataQuality`; name indicates sea-level height but the precise vertical datum remains a physical/SDK-doc validation item. |
| `KeyTakeoffLocationAltitude` | `Double` | Present | Altitude of takeoff location by name; not aircraft takeoff-relative height. |
| `KeyRelateToTakeOffPosition` | `XYZ` | Present | Three-axis relative position; axis convention and units require device validation. |
| `KeyAircraftVelocity` | `Velocity3D` | Present | Three components; coordinate frame and units require device validation. |
| `KeyHomeLocation` | `LocationCoordinate2D` | Present | Home-point horizontal coordinate. |
| `KeyIsHomeLocationSet` | `Boolean` | Present | Home-point availability indicator. |
| `KeyGPSIsValid` | `Boolean` | Present | GPS validity indicator. |
| `KeyGPSSignalLevel` | `GPSSignalLevel` | Present | Signal-level enum, not an accuracy estimate. |
| `KeyGPSSatelliteCount` | `Integer` | Present | Satellite count, not an accuracy estimate. |

## Runtime Classification Rules

Each subscribed key logs both support and value state:

- `KEY_UNSUPPORTED`: the connected product rejects the key.
- `KEY_NO_VALUE`: the SDK supports the key but currently returned `null`.
- `KEY_AVAILABLE_BUT_INVALID`: a value exists but failed type-specific
  semantic validation.
- `KEY_AVAILABLE_VALID`: value passed basic finite/range validation.

Latitude/longitude reject `null`, non-finite values, `0,0`, known DJI unset
sentinel coordinates, and values outside legal latitude/longitude ranges.
`LEVEL_0`, zero satellites, `GPSIsValid = false`, and a sea-level-height
quality value of zero or less are explicitly unusable. Velocity is valid only
as a finite vector and does not imply that position is valid. A valid raw
value is still not approved for AR propagation.

## Current Result

`POSITION_KEYS_SUPPORTED = true`

`POSITION_DATA_CURRENTLY_VALID = false`

All audited keys are supported by the Mini 3 Pro, but the indoor physical run
returned no usable position data: `AircraftLocation3D` and
`RelateToTakeOffPosition` were `null`; `GPSIsValid` was false; signal was
`LEVEL_0`; satellite count was zero; home location was an invalid sentinel;
and `HeightAboveSeaLevel.quality` was zero.

`TELEMETRY_TRANSLATION_CAPABILITY = NONE`

This is a runtime-data limitation, not an SDK-support limitation.

`FUTURE_LOCAL_NED_POSSIBLE = UNVALIDATED`

It can be considered only if a valid horizontal WGS84 source and one
understood, usable vertical source are confirmed from the same physical setup.
`KeyAircraftVelocity` alone is not full position.

`KeyTakeoffLocationAltitude` remains diagnostic-only and is marked
`UNVALIDATED_FOR_AR_POSITION`; its reference must be established separately.

`HasTelemetryPosition = false`

This remains intentionally false until the physical audit proves a complete
source. `RotationPropagationMode` and `SAFE_FOR_ROTATION_ONLY_PROPAGATION`
are unchanged.

## Pending Physical Test

```yaml
PENDING PHYSICAL TEST: outdoor GNSS validation

Wait for:
  GPSIsValid: true
  satelliteCount: "> 0"
  signalLevel: "> LEVEL_0"

Then inspect:
  - AircraftLocation3D
  - RelateToTakeOffPosition
  - HomeLocation
  - HeightAboveSeaLevel
  - TakeoffLocationAltitude
  - AircraftVelocity
```

No flight is required for the initial outdoor GNSS test if aircraft position
becomes valid while stationary. A later safe takeoff test may be needed to
determine whether `KeyRelateToTakeOffPosition` becomes valid only after takeoff.

## GNSS Data-Path Diagnostic

The Mini 3 Pro can be connected and provide attitude while a supported
FlightController position key still returns a cached default or no value. The
next diagnostic build therefore records, for every audited position key:

- the runtime `DJIKey` component/sub-component owner and indices;
- key value type and `canGet`/`canListen` capability;
- listener bind time and every old/new callback;
- one asynchronous `KeyManager.getValue` result, including full DJI error
  metadata when it fails.

`KeyAircraftAttitude` is logged side-by-side as the known-good control key.
`KeyProductType` and `KeyFlightControllerConnection` identify the connected
product and FlightController state at the same time. This is diagnostic-only:
the current `NONE` translation classification and `HasTelemetryPosition = false`
remain unchanged.

The imported MSDK 5.17.0 FlightController key list also exposes diagnostic
alternatives that are not subscribed by this audit: `KeyGnssExisted`,
`KeyFCGPSEnabled`, `KeyGPSModeFailureReason`, `KeyFusionPositioningLevel`,
`KeyVisionPositioningLevel`, `KeyMultiGNSSInfo`, `KeyPositioningDataSource`,
`KeyDronePositionSource`, `KeyHomeLocationWithType`, and `KeyGPSSNRInfo`.
Their availability and semantics must be established from the direct-get
results before any is added to a future position source.
