# Consolidated normal Android build (2026-11-21)

Latest successfully built normal-profile reference (2026-11-21):
`RoofCanopy-final-build-candidate.apk`, SHA256
`4853AFB320FC910962A2555A4B3DFF22F1330D7567252AFB5F9996396B098931`.
It was built with `CommandLineAndroidBuild.BuildApk`, without candidate defines.
Its functional predecessor, `RoofCanopy-fast-horizontal-tracking-candidate.apk`
(`AA9275D2CCA078D10D42D6F076DEA1BD930A0ADAB75A340678630C5547BD44D2`),
is historical physical-test evidence, not the latest build reference.
This configuration record is not a claim that all physical tracking problems
are solved. The November 21 telemetry position issue remains open. The previous
coordinate-frame reviews were read-only: W/B handling remains unchanged.

## One normal profile

Run Unity 6000.0.72f1 with `-batchmode -quit -buildTarget Android`, the existing
persistent licensing/UPM supervisor, `-executeMethod CommandLineAndroidBuild.BuildApk`
and an explicit `-outputPath`. Existing key provisioning is unchanged; never put
the key in a command line, report or commit. ARM64, IL2CPP, minSdk 24, targetSdk 36,
package `com.sok9hu`, scene assets and URP configuration are unchanged.

`BuildValidatedTrackingCandidateApk` is a compatibility alias, not another profile.
The historical border build wrappers also delegate to the same normal build.
Normal `extraScriptingDefines` is empty; no DJI defines are added to PlayerSettings.

## Permanent policies

The following former compile-time gates no longer control shipping functionality:

- `DJI_APRILTAG_BORDER_48_EXPERIMENT`: four-tag reacquisition at 48 px; initial
  acquisition and one-to-three-tag observations retain 64 px.
- `DJI_APRILTAG_BORDER_RETENTION_EXPERIMENT`: conditional four-tag VISUAL retention.
- `DJI_APRILTAG_STALE_JUMP_EXPERIMENT`: existing stale, independently checked cluster.
- `DJI_APRILTAG_NO_MARKERS_QUALITY_LOSS_EXPERIMENT`: genuine newer zero-marker loss.
- `DJI_VALIDATED_TRACKING_CANDIDATE`: source freshness, bracket waiting, validated
  250 ms dwell and 6-degree long-outage orientation protection.
- `DJI_ROOFCANOPY_WALL_SHADOW_EXPERIMENT`: verified model-derived wall settings on
  Android players only. The Editor remains OFF; geometry tests opt in explicitly.

Historical helper/asset names remain to avoid unnecessary API and asset migration.
Explicit pure-policy arguments and one Editor-only safety switch support existing
OFF/ON comparison tests. They do not create an Android runtime selector.
The redundant rotation developer override is removed: its safety constant was
already true, so removing the impossible rejection branch does not change behavior.
TEMP and legacy guarded-GNSS modules are retained; this is not algorithm cleanup.

Horizontal defaults remain 0.04 m / 0.25 s, two distinct directional samples,
2.5 m/s correction cap, SAT23/21 hysteresis and 300 ms freshness. RAW vertical,
telemetry histories, model placement, culling, HUD and logging cadence are unchanged.

## Explicit diagnostics only

`BuildDetectorCaptureDiagnosticApk` adds only `DJI_EXACT_DETECTOR_CAPTURE`.
`BuildRelativePositionDiagnosticApk` adds only `DJI_RELATIVE_POSITION_EXPERIMENT`.
Both retain all normal tracking policies. Neither capability is enabled by the
normal build and neither changes Normal/Diagnostic logging selection.
The existing explicit Diagnostic-session selection is still required before those
capabilities can collect data; choosing a build entry does not enable verbose logging.
The prebuild guard rejects persistent DJI defines to prevent hidden profile drift.

## Bridge artifact contract

Build the sibling `DJIBridge` with its existing Gradle wrapper and JDK:
`./gradlew.bat assembleRelease testReleaseUnitTest` (no clean/cache deletion).
Native multi-start defaults ON and an explicit OFF Release request must fail.
Standalone native replay tests may still compile OFF explicitly.

Deploy only `build/outputs/aar/DJIUnityBridge.aar` to
`Assets/Plugins/Android/DJIUnityBridge.aar`. Its feature manifest is
`assets/roofcanopy-bridge-features.json` (schema 1), with native-library SHA256s.
The validated bridge source revision is
`143c377766fc00a959c16cf5f6c88d48df717ef9`; deployed AAR SHA256 is
`E227813FF1F20B28A827499D678BE0B7969817F7BCBA3A7B0391E6FFB136887D`.
`FinalBridgeArtifactGuard` verifies those hashes and independently verifies the
ARM64 JNI feature getter's ON machine code. Missing/stale/OFF artifacts fail closed.

## Regression contract

Keep every baseline test full name/case. Tests formerly asserting Editor OFF now
assert the equivalent normal ON policy, including unchanged independent quality
gates; historical OFF comparisons explicitly opt out in Editor fixtures only.
NUnit numeric discovery IDs are not stable across added tests; compare full names.
New tests cover normal/diagnostic profile parity, missing/OFF/mismatched AARs and
actual native feature verification. Record executed results, source hashes and APK
verification outside the repositories in `RoofCanopyFinalBuildConsolidation-20261121`.
