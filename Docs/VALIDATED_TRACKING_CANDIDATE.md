# Validated tracking restoration candidate (2026-11-20)

Historical candidate rationale follows. Since the 2026-11-21 build consolidation,
these accepted policies are normal functionality without experimental scripting
defines. Use `CommandLineAndroidBuild.BuildApk`; the former candidate entry is only
an alias to that method. Persistent PlayerSettings and normal logging are unchanged.
The Release bridge requires native multi-start ON, verified against the actual
native binary before an Android build. See [final build configuration](FINAL_BUILD_CONFIGURATION.md).
This consolidation is not a new claim of physical tracking accuracy.

## Feature decision matrix

| Feature | Nov 11 APK | Nov 20 release | Candidate | Evidence and limitation |
|---|---|---|---|---|
| Native four-tag multi-start | ON | OFF | ON | Replays all recognized tags' initial poses against the same 16 corners. Existing 132-fixture OFF/ON tests cover finite/depth checks, 1-3 tag parity and deterministic selection. Better RMS is not physical ground truth; see orientation guard below. |
| Four-tag reacquisition border 48 px | ON | OFF | ON | Initial acquisition and 1-3 tags retain 64 px. Prior physical logs included accepted 48-64 px poses; independent quality, confirmation and dwell remain required. Clipping nearer than 48 px is not allowed. |
| Four-tag VISUAL border retention | ON | OFF | ON | Avoids returning to the 64 px requirement immediately after 48 px reacquisition; requires prior high confidence and four tags. Other quality gates remain independent. |
| Stale translation-jump cluster | ON | OFF | ON | Only stale four-tag reacquisition with independently good quality. Anchor-bounded cluster, timestamp ordering, confirmation and dwell stay intact. Does not directly grant authority. |
| Explicit NO_MARKERS quality loss | ON | OFF | ON | Genuine newer zero-detection results revoke quality without discarding the exact accepted baseline. Out-of-order/generation fences remain. Short detector gaps can cause additional safe telemetry transitions. |
| Model-derived first-surface wall shadow | ON | OFF | ON | Verified geometry asset and FBX identity must match. Ground/wall partitioning uses the existing implementation. This is NOT a measured real wall. |
| RAW vertical | ON via define | ON by default | ON by default | No RAW experiment define required or added. Altitude source, freshness, timestamps and transforms are unchanged. |

All other optional experiments and exact detector capture stay disabled. The bridge
does not change detector settings, the camera model, optimization or 1-3 tag paths.
The restoration does not alter GNSS gates, HOLD, smoothing, HUD, rendering bases,
model transforms, lighting, geometry, materials or sunlight direction.

## Required visual-source and reacquisition safeguards

The former `DJI_VALIDATED_TRACKING_CANDIDATE` policy is now always active in
Android players. An Editor-only switch remains for historical comparison fixtures.
The two narrow safeguards are:

- Positive results must have a valid source time, be newer than the accepted pose,
  respect listener/frame ordering, and arrive within the existing two-second visual
  freshness budget. Delivery cannot rejuvenate an old frame. Rejected results do
  not replace the accepted visual/telemetry baseline.
- During live long-outage telemetry reacquisition, require the same source-time
  telemetry bracket and existing 6-degree rotational consistency bound used in the
  short window. Do NOT apply the short window's 1 m translation bound: a 10 m
  translation correction can still pass if independently aligned rotation and all
  other quality/confirmation/dwell conditions pass. A missing, still obtainable
  endpoint now waits for at most the existing 0.5 s pending timeout. The original
  image source time is retained in a bounded queue (existing 32-sample limit).
  Telemetry arrival and Update retry without granting dwell for waiting or duplicate
  callbacks. Each image is checked against a bracket no older than 250 ms per side.
  Unavailable evidence that cannot recover, invalid/expired data, or an inconsistent
  orientation cancels the candidate dwell, not the outage clock. Already checked
  evidence survives a temporary missing endpoint. Dwell is the source-time span
  of distinct, checked images (at least 250 ms, no source gap above 0.5 s); all queued
  images must pass before promotion. A later correct branch establishes its own dwell.

Native RMS minimization proposes a pose; it does not grant render authority. There
is no new native temporal estimator. A sustained 45/90-degree branch inconsistent
with the paired telemetry cannot pass merely by remaining stable. A real large
camera turn can pass when the independent prediction follows it.

This guard deliberately favors orientation safety over availability. If independent
attitude is wrong by more than 6 degrees, a correct visual orientation can also be
held back during a live outage. No guarantee is made for ambiguous first acquisition,
fresh initialization without a surviving baseline, or a wrong branch consistent
with incorrect telemetry. Do not equate the guard with universally solved planar
ambiguity. Those limits require physical validation and richer evidence before any
claims of universally reliable tracking.

The candidate writes bounded `ReacquisitionEvidence` events (at most 1 Hz plus
acceptances) and `AppliedOrientationEvent` on render-source transitions or significant
rotation changes (6 degrees since the last event, at most 1 Hz otherwise). These
reuse the normal asynchronous session writer, contain quaternions and sample ages,
and contain no GNSS/location values. This is diagnostic only: it does not change
the rotation estimator or view conversion. The suspected later tilt in the Nov 20
flight remains unresolved; no new VISUAL pose was accepted after 15:07:50.893 CET.

## Wall coordinates

The geometry asset stores the wooden rear-rafter plane at content-local
`Y=-0.0036760657 m`; it must not be replaced by a board-relative constant. The
common corner parent contributes `(-0.18,+0.18,0) m`, giving board-relative
`Y=+0.1763239343 m`. The same parent moves the model and both shadow receivers
exactly once. The placeholder `0.25 m` wall offset stays disabled in the source
settings. The runtime copy uses the verified model-derived asset.

The fixed ray remains `(+0.409576246,+0.573576344,-0.709406426)`. The mask is baked
for the runtime first-surface settings rather than reusing a ground-only mask.
Ground/wall ownership prevents overlapping double projection. The plexiglass
overhang and the mismatch between model-derived wall and actual physical wall
remain reported limitations; the board was not physically moved in the Nov 20 test.

## Validation and evidence

Full Unity EditMode tests include the original localization/shadow/placement suite,
all feature interactions, new candidate source-time/ordering/build configuration
checks, and long-outage rotation safety with translation drift and alternating
branches. Native replay and JVM tests are run separately. Normal Editor policy
getters are ON except the Android-only wall receiver. Explicit historical-policy
fixtures may compare OFF behavior; Android safety cannot be disabled that way.
The Android build's compiled getters must also be checked.

Evidence, executed counts, native runtime comparison and candidate APK hash belong
in `C:/Users/betti/Desktop/git/RoofCanopyRestoreValidated-20261120/`. Do not replace
or install the Nov 20 APK automatically. No physical flight is performed by Codex.
