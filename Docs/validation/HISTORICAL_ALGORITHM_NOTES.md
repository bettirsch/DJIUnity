# Historical Localization Algorithm Notes

Extracted on 2026-10-16 for Cleanup Step 5C. This is a durable description of
existing code/history and archived experiments, not a new implementation or
an authorization to remove production behavior. Raw inventories remain in
[the manifest](THESIS_EVIDENCE_MANIFEST.csv); attribution corrections and the
21-session ledger are in [the index](THESIS_EVIDENCE_INDEX.md).

## Reading the Evidence

`T_A_B` maps coordinates from B to A. Navigation uses North/East/Down.
World board/model transforms are immutable; a diagnostic camera candidate is
not an authoritative localization update. A JSON field being present does not
mean its validity flag is true or that its numerical residual is usable.

All times used for physical comparisons are Android monotonic nanoseconds.
Session IDs are UTC naming metadata, not an installed source revision. No
archived file provides an APK/source hash join sufficient for `EXACT:<sha>`.
The manifest ranges identify compatible implementation lineage, including
singleton ranges, not proven installed binaries. Where capture chronology and
the first committed schema disagree, identity remains `UNKNOWN`.

The raw audit parsed every inventoried JSONL file, checked the union of actual
serialized field paths and record types, and verified all 114 size/SHA256 pairs.
Old source timestamps in a newly started logger can be a history replay, not a
new DJI callback. Logcat can contain other sessions/PIDs; join against the JSONL
session, time span and relevant app process before counting events.

## GNSS+Velocity Complementary Experiment

### Purpose and Source

The experiment asked whether fresh aircraft N/E velocity could provide smooth
short-term motion between absolute GNSS updates while GNSS corrected drift.
It ran beside the validated RAW horizontal GNSS candidate, not in rendering.
It did not use vertical velocity or mutate `WorldLocalizationState`.

Reference implementation:
`Assets/Scripts/HorizontalGnssVelocityComplementaryEstimator.cs`, introduced
in `3be10dc`; integration and reacquisition contracts are in
`Assets/Scripts/DjiWorldLocalizationShadow.cs`. `f116b71` introduced the raw
GNSS candidate and `413147b` extended its diagnostic lifetime through NONE.
The failed velocity experiment is distinct from the later GNSS-only smoother
introduced in `eca6ae8` and promoted in `823b520`/`d0ddf7a`.

The raw candidate owns exact visual-baseline resolution, causal position lookup,
WGS84/ECEF-to-local-NED conversion and fixed `R_world_navigation`. Fusion reuses
that candidate rather than repeating geodesy or selecting another baseline.

### Equation and Configuration

Let `x` be fused N/E displacement from the visual baseline, `g` the causal RAW
GNSS N/E displacement and `dt=(targetTimestamp-lastPredictionTimestamp)/1e9`.
Initialization sets `x=(0,0)` at the visual baseline timestamp.

```text
dv = integral(v_NE dt) over the new interval, using ZERO_ORDER_HOLD
     only if the complete velocity integration interval passes freshness

x_pred = x + dv          if velocity interval is valid
x_pred = x               otherwise

alpha = 1 - exp(-dt / tau)
x_next = x_pred + alpha * (g - x_pred)    if GNSS is fresh
x_next = x_pred                          otherwise

tau = 1.0 s
maximum velocity held-sample age = 500 ms
maximum causal GNSS baseline/target age = 300 ms
```

The GNSS correction occurs per monotonic estimator evaluation, not exclusively
on a newly arrived GNSS callback. The latest fresh causal target can therefore
be corrected toward on several evaluations. Identical target timestamps give
`dt=0`, so neither integration nor correction is applied twice. Backward target
timestamps fail. Internal velocity gaps and endpoints must all satisfy the
velocity integration helper; one fresh target sample is not enough by itself.

This estimator has no correction-speed clamp. Do not confuse its `tau=1 s`
with the later production GNSS-only `tau=0.5 s`, maximum `2.5 m/s` smoother.

### Dropouts and Lifetime

Stale/missing velocity contributes no increment, but fresh GNSS can still
correct the estimate. Stale/missing GNSS contributes no correction, but a fully
fresh velocity interval can advance it. With neither available the internal
estimate holds; validity can persist for at most the 500 ms recent-input window
after the last reliable input, then becomes invalid. Update timestamps still
advance, so a later callback does not retroactively integrate a stale gap.

Neither temporary source dropout recaptures the visual baseline or resets the
estimator. Diagnostic state survives production ROTATION_PROPAGATED -> NONE.
Authoritative visual reacquisition compares RAW, FUSED and held candidates
against the same visual world camera, then closes that outage. A new outage
initializes a new baseline; it does not carry old displacement into the next.

### Actual Evidence and Rejection Rationale

`20261012T064147088Z` is NOT fusion endpoint evidence: its 294
`WORLD_HORIZONTAL_GNSS_SHADOW` records contain no `fusedCandidateValid`,
`fusedNorthM`, or fused reacquisition residual fields.

The correct first paired capture is `20261012T153204510Z`:

| Visual baseline timestamp ns | RAW error m | FUSED error m | Held error m |
| --- | ---: | ---: | ---: |
| 4610725180327079 | 0.1353473 | 0.13506341 | 0.07893482 |
| 4610743758729912 | 0.185237855 | 0.185510308 | 0.249978527 |
| 4610779584374501 | 0.5805696 | 0.5805656 | 0.5928666 |

Read `rawReacquisitionHorizontalErrorM`,
`fusedReacquisitionHorizontalErrorM`, and
`heldPositionReacquisitionHorizontalErrorM` from that session's latency JSONL.
The logger repeats cached endpoint summaries; count distinct visual baselines
with finite paired residuals, not every periodic record. This yields three
serialized comparisons, not every outage in the flight. The largest absolute
RAW/FUSED endpoint difference is only 0.00028389 m in these three pairs.

That session's raw velocity file has 169 callbacks, effective inter-first/last
rate `(169-1)/105.176816771 = 1.59731018 Hz`, and longest gap
10.408988146 s. The 500 ms freshness requirement cannot turn such an irregular
source into dense production motion. Four later paired summaries in
`20261013T073405787Z` also have almost equal RAW/FUSED endpoints; they are a
separate capture and are not pooled into the three-event result above.

The retained negative conclusion is narrow: this available velocity delivery
did not justify a production smoothing dependency. It does not prove that
complementary filters or aircraft velocity are universally unsuitable.
The old 32.6% fresh coverage and 5.30 s p95 age lack a reproduced sampling
definition here and remain `SUMMARY_ONLY`, not newly recalculated facts.

Evidence archived = YES; algorithm documented = YES; future physical test needed
to retire only this historical diagnostic = NO; eligible for scoped retirement
= YES. Preserve RAW GNSS, shared histories, geodesy and production smoothing.

Provenance: `RAW_VERIFIED_COMMIT_RANGE` (`3be10dc` schema lineage).

## Vertical Source-Selection Experiment

### Candidates, Timing and Comparison

`8a31b20` introduced the diagnostic comparison; `3dd0a9a` completed callback
and endpoint serialization. Bridge `dea95d9` completed the corresponding raw
instrumentation. Production selection happened later in `82636b5`; it is not
part of the source-selection flight.

Candidate implementations are in
`Assets/Scripts/VerticalLocalizationSourceDiagnostics.cs` and
`Assets/Scripts/DjiVerticalTelemetryHistory.cs`; the logger writes both
`VerticalSourceSample` records and periodic `verticalDiagnostics` objects.

| Candidate | Raw sample | Hypotheses against visual navigation Down |
| --- | --- | --- |
| KeyRelateToTakeOffPosition | unvalidated DJI X/Y/Z | each of +/- deltaX, +/- deltaY, +/- deltaZ |
| KeyAltitude | scalar altitude | +/- deltaAltitude |
| KeyHeightAboveSeaLevel | height plus raw quality | +/- deltaHeight, with availability/quality checked |
| KeyAircraftLocation3D.altitude | scalar altitude | +/- deltaAltitude |

At the exact production-selected authoritative visual baseline, cache
`worldFromCamera0`, its timestamp and the fixed world-from-navigation rotation:

```text
R_world_navigation = R_world_camera0 * inverse(R_navigation_camera0)
visualDeltaWorld = position(worldFromCamera1) - position(worldFromCamera0)
visualDeltaNavigation = inverse(R_world_navigation) * visualDeltaWorld
visualDeltaDown = visualDeltaNavigation.z

sourceDelta = currentSource - baselineSource
positive-down residual = abs(sourceDelta - visualDeltaDown)
positive-up residual   = abs(-sourceDelta - visualDeltaDown)
```

Pose 1 is the newly accepted authoritative visual pose, not a raw/pending PnP
candidate or nearest 500 ms telemetry reconstruction. Each source uses its own
latest-at-or-before timestamp at both ends, with no future sample lookup.
Source timestamp/age and baseline/current values are serialized alongside the
fixed rotation and visual N/E/D delta.

Important distinction: these diagnostic histories select causally but do not
apply a 300 ms age cutoff inside `VerticalLocalizationSourceDiagnostics`.
Stale KeyAltitude observations are deliberately retained for comparison.
For the fresh physical comparisons below, analysis requires both Location3D
endpoint ages <=300 ms, matching the subsequent production freshness contract.
Do not treat `deltaAvailable` alone as proof of freshness.

An unavailable relative-position candidate can still serialize residual numbers
computed from its zero placeholder. Ignore them unless `deltaAvailable=true`.
Likewise, cached endpoint summaries in periodic snapshots are not new maneuvers:
de-duplicate by visual baseline and reacquisition timestamps.

`Begin` resets source baselines for a new outage; `Complete` computes the final
visual delta and deactivates the interval. The diagnostic lifetime survives
production NONE, and a later outage gets fresh source baselines. It never
selects a production source or mutates world/model transforms.

### Two Separate Physical Phases

| Session | Location3D source samples | Median source interval ms | Distinct endpoint pairs | Fresh pairs with abs(visual Down)>=0.20 m | Positive-up residual median m |
| --- | ---: | ---: | ---: | ---: | ---: |
| 20261014T181720308Z | 826 | 213.873577 | 17 | 7 | 0.03102237 |
| 20261014T182025009Z | 936 | 214.871923 | 20 | 8 | 0.052459121 |

Counts refer to logged, session-deduplicated `VerticalSourceSample` records,
including initial bounded-history replay. They are not all callbacks that
occurred after logger startup. Phase A and phase B are not pooled into one
accuracy distribution. All seven/eight fresh meaningful pairs have the sign
predicted by positive-up altitude. The respective positive-up residual p95s
are 0.08614802 m / 0.151825154 m using linear interpolation between ordered
sample ranks; motion/lever-arm contamination is not compensated.

Representative phase-B pair:
baseline `4793604527642401`, reacquisition `4793606201359208` ns;
Location3D altitude 1.5 -> 1.8 m; visual Down -0.2682859 m;
source ages 79.567923 / 138.903039 ms;
positive-up residual 0.03171411 m versus positive-down 0.568285942 m.

### Selected and Rejected Sources

Location3D altitude is the selected source on this Mini 3 Pro/data set because
it has causal fresh samples, approximately 4.7 Hz source cadence and consistent
positive-up response on multiple upward and downward maneuvers. Production
uses `deltaNavigationDown = -(currentAltitude-baselineAltitude)`, not the raw
altitude as a world coordinate. This does not identify an absolute altitude
datum or claim centimetre-level universal accuracy.

KeyAltitude has 64 / 119 source records. Its median interval during callback
bursts is about 232 ms, but endpoint ages are often seconds or tens of seconds
old; burst cadence alone is misleading. It often matches the same numerical
change as Location3D without adequate endpoint freshness, so it was not selected.

RelateToTakeOffPosition has zero raw source records and unavailable endpoint
values: no axis hypothesis can be validated from this capture.

HeightAboveSeaLevel has only two raw quality-zero samples, both
`QUALITY_NON_POSITIVE`, in each session. The source timestamps are identical
across phases (`4793398671554241` and `4793398688600933` ns): these are the
same old samples replayed into a new logger session, not four new callbacks.
No fresh valid sea-level-height source was demonstrated.

Neither source-selection session contains `verticalProductionSource` or
`verticalLocalizationState`. The production vertical field family is first
present among these archived sessions in `20261015T054613552Z`, compatible
with `82636b5` through `81cdbb7`. Do not cite phase B as a rendered-vertical
validation. Source selection and production-policy validation remain separate.

Evidence archived = YES; algorithm documented = YES; future physical test needed
to retire only source-comparison diagnostics = NO; eligible = YES. Preserve
Location3D altitude telemetry, its production smoother/freshness and all shared
horizontal/vertical source transport. Do not remove production vertical fields
as though they were historical source-selection hypotheses.

Provenance: `RAW_VERIFIED_COMMIT_RANGE` (`3dd0a9a`/`dea95d9` schema lineages).

## Historical Reacquisition Investigation

### Original Closeness Policy

`5da6137` introduced a stable visual return after rotation propagation:
HighConfidence quality for at least 250 ms plus spatial agreement with the
rotation-only predicted board pose. Default position limit was 1.0 m and
rotation limit 6 degrees, intentionally conservative rather than visual-quality
threshold changes. The comparison used the production visual/telemetry
rotation baseline; it did not estimate true outage translation.

`da3caa8` added horizontal comparison diagnostics, and `b6276e5` added hybrid
candidate scoring. RAW GNSS/fresh velocity/hybrid error comparisons were
measurements, not new render authority. The later score margin (0.03 m in
`DJI_REACQUISITION_HORIZONTAL_DIAGNOSTIC`) classified the diagnostic comparison;
it was not an extra production visual-admission threshold.

### Two Distinct Failures

The old post-NONE failure was comparing a new localization against stale
pre-outage jump/pose state after the short propagation window had ended.
`eb72e1d` reset the localization-local reference state and introduced fresh
relocalization semantics. The October 5-7 raw sessions postdate that fix;
they cannot prove the original pre-fix stale-reference mechanism. Source history
is the evidence for that earlier policy; its old physical counts remain
`SUMMARY_ONLY` where the original session is not in this 21-session archive.

Long-lived TELEMETRY_PROPAGATED, introduced by `d0ddf7a`, exposed another failure
in `20261013T073405787Z`. A real camera translation or accumulated GNSS error
can make a perfectly valid returned visual pose disagree with a short-window
zero-translation prediction. Closeness to that prediction is not an appropriate
criterion for long-lived relocalization. The pre-fix capture contains production
and outage state; exact counts/causal attribution are not newly replayed here.

There was also a lifetime-ordering defect: telemetry paired with a provisional
visual candidate could reach propagation sample handling before accepted VISUAL
handover and stop/reset propagation. A provisional candidate is not authoritative.

### Fix and Production Semantics at `9b15ceb`

The state names below are historical. The current implementation uses one
telemetry render source and selects the same short/long checks by elapsed time;
see [current telemetry propagation](../TelemetryPropagationValidation.md).

`9b15ceb` adds two distinct protections in
`Assets/Scripts/DjiBoardVisionProvider.cs` and
`Assets/Scripts/RotationPropagationRenderPolicy.cs`:

- `ShouldPreservePropagationForProvisionalVisual` leaves the live propagation
  baseline/lifetime intact while render authority is ROTATION_PROPAGATED or
  TELEMETRY_PROPAGATED. Only accepted promotion owns the visual handover.
- For TELEMETRY_PROPAGATED, `IsLongLivedTelemetryReacquisitionReady` uses the
  fresh-relocalization HighConfidence + 250 ms dwell predicate. It skips the
  old short-window position/rotation closeness comparison.

```text
short-window ROTATION_PROPAGATED return:
    normal visual quality + stable dwell + existing pose agreement

fresh relocalization after NONE:
    normal visual quality + stable dwell, new localization baseline

long-lived TELEMETRY_PROPAGATED return:
    normal visual quality + stable dwell, no stale closeness veto

pending/rejected candidate:
    do not replace propagation baseline or terminate propagation

accepted authoritative visual:
    update camera localization from the accepted visual pose
    close the outage and initialize future outage state from that pose
```

The exception does not loosen RMS/corner/border/marker quality, add blending,
or permit raw PnP candidates to mutate authoritative world state.

`20261014T060059796Z` logcat explicitly contains
`DJI_VISUAL_REACQUISITION_PENDING` and `...ACCEPTED` with
`mode=LONG_LIVED_TELEMETRY poseConsistency=SKIPPED` on the relevant Unity PID.
For example, the local-time 08:02:01.094 accepted event has dwell 334.2 ms.
Its session JSONL includes the same fix-lineage temporal consistency fields.
This is post-fix evidence, independent from the older pre-NONE investigation.

Evidence archived = YES for the source-history mechanism and named pre/post
long-lived captures; algorithm documented = YES; future physical test needed
for retiring historical comparison/scoring output only = NO; eligible = YES.
This explicitly does NOT authorize removal of active admission predicates,
provisional-candidate protection, exact production-baseline history resolution,
authoritative promotion, outage ground truth or final production fallbacks.

Provenance: `RAW_VERIFIED_COMMIT_RANGE` for the long-lived fix retest;
the older unarchived pre-NONE physical counts remain `SUMMARY_ONLY`.

## Reproducing and Preserving These Claims

1. Import the CSV without rewriting it; for every semicolon-separated inventory
   item verify `<filename>|<bytes>|<sha256>` against the exact session directory.
2. Parse each JSONL line independently. Record malformed lines rather than
   dropping the rest of the file. Require `telemetrySessionId` to match the row.
3. Separate record families before counting. An accumulator's sample counter,
   periodic JSON record count, cached endpoint record and independent outage
   count are different quantities.
4. For FUSED comparisons, use finite paired residuals and unique visual baseline
   timestamps. For vertical comparisons, use unique baseline/reacquisition pairs,
   valid source endpoints and separately reported ages/quality.
5. Keep phases separate. For the fresh meaningful vertical subset require
   `abs(visualDeltaDownM)>=0.20` and both endpoint ages <=300 ms. Report the
   exclusion rule, not just the resulting favourable residual.
6. For source cadence use unique actual source timestamps, not logger timestamps.
   Check for initial history replay and identical source timestamps in later
   sessions before claiming new callbacks or a fresh quality state.
7. Use `git show <commit>:<path>` to recover the historical implementations and
   `git log -S '<serialized field>' -- <path>` to check schema introduction.
   A feature-introducing commit is not proof that its APK was installed.

No implementation, test, package, setting, raw evidence file or build artifact
was changed as part of this extraction. No build or physical test is required
for the documentation repair itself.
