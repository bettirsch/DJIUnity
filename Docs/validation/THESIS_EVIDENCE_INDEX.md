# Thesis Validation Evidence

## Archive Contract

The physical archive was frozen by `633eb48`. This metadata repair follows
`4fbaa26`; it does not change the captured experiments or raw files.

Raw data stays at
`C:\Users\betti\Desktop\git\physical-logs\<session-id>\`.
The manifest inventories 21 directories and 114 files with byte sizes and
SHA256 hashes. No raw file is copied into Git, normalized, renamed, repaired,
or deleted. The original inventory strings remain unchanged.

On 2026-10-16 all 114 sizes/hashes were rechecked. All directories exist.
Parsed JSONL records name only their own indexed session. Comparable
top-level monotonic event/logger timestamps do not regress; nonempty companion
streams have overlapping time ranges. Source sample timestamps may precede
logger startup because bounded histories are replayed once per session.

Known capture limitations are retained: one malformed final line in each of
the position and velocity files for `20261005T154958076Z`; an empty position
callback file for `20261011T164644346Z`; no raw logcat in some sessions.
Logcat may contain historical PIDs; session JSONL and monotonic joins take
precedence over unfiltered logcat counts.

## Provenance Vocabulary

- `RAW_VERIFIED`: the specific observation is reproducible from named raw
  records; the installed commit can still be unknown.
- `RAW_VERIFIED_COMMIT_RANGE`: raw observation plus a best-supported
  implementation-lineage range from schema/history. Not an APK-to-Git proof.
- `SUMMARY_ONLY`: a historical report or physical interpretation is retained,
  but this repair has not reproduced it from the named raw records.
- `INCONCLUSIVE`: missing/contradictory evidence prevents the claimed conclusion.

`corresponding_commit` uses `EXACT:<sha>`, `RANGE:<oldest>..<newest>`,
or `UNKNOWN`. No exact deployed SHA was recovered: these captures do not carry
a binary-to-source fingerprint. Ranges are inclusive, schema-compatible
lineage attribution, not a claim that every intermediate revision was deployed.
A singleton range is still not an exact build identity. Absence of a family is
used only where that revision's logger would emit it unconditionally.
The manifest's `commit_evidence` records the reasoning and limitations.

The nine former `SUMMARY_ONLY` commit cells became eight ranges and one
`UNKNOWN`. A further previously asserted commit was downgraded to `UNKNOWN`,
leaving two unknown identities overall. Historical metrics may remain
`SUMMARY_ONLY` even when the experiment's raw schema is verified.
Physical configurations in the original manifest are reported test context,
not measurements independently established by a field name.

## Session Ledger

Duration is now the last minus first periodic `monotonicTimestampNs`
snapshot, in seconds; it includes startup/hold time and is not flight time.
For newer telemetry files containing callback records, their logger timestamps
also share the same envelope. These durations replace inconsistent older
callback-span/summary durations.

T/O/L = telemetry JSONL lines / outage JSONL lines / latency JSONL lines.
V/R/G = visual equivalence / rotation equivalence / persistent-world horizontal
GNSS records. Neither record counts nor repeated cached summaries are counts
of independent reacquisitions.

| Session | Duration s | T / O / L | V / R / G | Raw-supported scope |
| --- | ---: | ---: | ---: | --- |
| `20261004T053626893Z` | 393.719 | 748 / 0 / 0 | 0 / 0 / 0 | raw callback telemetry baseline |
| `20261004T155159662Z` | 305.735 | 576 / 0 / 0 | 0 / 0 / 0 | runtime horizontal-mode telemetry baseline |
| `20261004T180245204Z` | 147.295 | 285 / 0 / 0 | 0 / 0 / 0 | guarded GNSS transition/reacquisition capture |
| `20261005T154958076Z` | 422.293 | 740 / 25 / 0 | 0 / 0 / 0 | self-contained visual outage capture baseline |
| `20261006T160453905Z` | 227.709 | 421 / 16 / 0 | 0 / 0 / 0 | visual outage and saved-handover capture |
| `20261007T161100474Z` | 177.126 | 343 / 10 / 0 | 0 / 0 / 0 | pre-latency outage capture |
| `20261007T163950587Z` | 81.773 | 158 / 4 / 407 | 0 / 0 / 0 | DJI video/OES latency instrumentation capture |
| `20261008T153514799Z` | 396.653 | 765 / 25 / 1094 | 0 / 0 / 0 | temporal predictor diagnostics |
| `20261009T054234382Z` | 114.696 | 222 / 2 / 755 | 84 / 0 / 0 | persistent-world visual equivalence |
| `20261009T091819301Z` | 141.143 | 273 / 17 / 668 | 110 / 0 / 0 | persistent-world visual render A/B |
| `20261009T162858883Z` | 286.127 | 508 / 8 / 379 | 131 / 130 / 0 | rotation-world equivalence before exact baseline sync |
| `20261011T164644346Z` | 116.298 | 225 / 25 / 446 | 89 / 87 / 0 | exact rotation-baseline synchronization retest |
| `20261012T052758539Z` | 170.325 | 329 / 35 / 709 | 121 / 119 / 165 | long-lived raw horizontal GNSS shadow |
| `20261012T064147088Z` | 304.543 | 588 / 35 / 1371 | 256 / 246 / 294 | long-lived raw GNSS and velocity callback capture |
| `20261012T153204510Z` | 125.636 | 243 / 13 / 483 | 101 / 98 / 122 | raw GNSS versus complementary fusion diagnostics |
| `20261013T073405787Z` | 394.656 | 591 / 14 / 1278 | 248 / 247 / 296 | long-lived production horizontal propagation before fix |
| `20261014T060059796Z` | 288.286 | 557 / 26 / 1199 | 244 / 238 / 279 | long-lived propagation and reacquisition fix retest |
| `20261014T181720308Z` | 182.533 | 1244 / 17 / 698 | 144 / 144 / 176 | vertical source selection phase A |
| `20261014T182025009Z` | 155.291 | 1357 / 20 / 668 | 138 / 137 / 151 | vertical source selection phase B |
| `20261015T054613552Z` | 255.544 | 1780 / 26 / 1008 | 222 / 214 / 248 | combined production horizontal and vertical flight |
| `20261016T115100463Z` | 123.800 | 1064 / 14 / 620 | 120 / 113 / 121 | horizontal quality-signal diagnostics |

Per-row commit reasoning, schema evidence, result scope and provenance quality
are in [the manifest](THESIS_EVIDENCE_MANIFEST.csv).

## Corrected Attributions

- `20261011T164644346Z` is a Step 7B exact-baseline synchronization retest:
  87 rotation records, final matched counter 230, and zero world-GNSS records.
  It does not validate `f116b71` horizontal GNSS. The local Step 8B APK/build
  timestamps are later than this capture, corroborating the schema boundary.
- `20261014T182025009Z` is vertical source-selection phase B, like phase A
  `20261014T181720308Z`. Neither contains `verticalLocalizationState` or
  `verticalProductionSource`. Production vertical evidence is instead in
  `20261015T054613552Z` and the later quality run.
- `20261012T064147088Z` contains 294 RAW world-GNSS records and no FUSED
  fields. It is not the GNSS+velocity endpoint A/B. That evidence is in
  `20261012T153204510Z`, which has FUSED fields but no production-horizontal
  fields. Its former production-policy label was also incorrect.
- The visual equivalence JSONL in `20261009T054234382Z` requires the
  `f473f39` logger addition, not merely the `352a1db` computation commit.
- `20261015T054613552Z` includes production vertical fields from
  `82636b5` or its descendants; the former `0137d59`-only attribution
  cannot explain them.
- Timing-only capture `20261007T163950587Z` precedes the commit that first
  contains those logger fields (`9cb3c06`), and lacks its temporal fields.
  Its exact precommit build lineage is unknown.
- `20261012T052758539Z` already has long-lived fields first committed in
  `413147b`, but its nominal capture start precedes that commit. A precommit
  build or clock/history discrepancy is unresolved. Raw content remains usable;
  the installed source identity is `UNKNOWN`.
- The October 5-7 sessions are not evidence of the original pre-`eb72e1d`
  post-NONE reset defect: they are later than the reset implementation.
  That old mechanism is documented from code history, not invented raw evidence.

## 1. Video and Temporal Alignment

`20261007T163950587Z` has 407 latency records;
`20261008T153514799Z` has 1,094 with temporal fields. CPU arrival,
detector/PnP completion, Unity delivery/application and OES arrival/latch are
observable. `sameDecodedFrameProven=false` must not be presented as matched
decoded video frames. Temporal performance improvements are not re-proven here.

Provenance: `RAW_VERIFIED`.

## 2. Visual Persistent-World Equivalence

`20261009T054234382Z` ends with 671 accumulated samples:
position max `8.842838e-7 m`, rotation max `0 deg`, view-matrix max
`8.34465e-7`. The A/B run `20261009T091819301Z` has latest counter 560,
with corresponding maxima `1.30799606e-6 m`, `0 deg`,
`1.1920929e-6`. These are accumulator maxima/counters, not counts of JSONL
summary records. Physical board removal is separate from VISUAL-path parity.

The visual measurement implementation was retired in `4fbaa26`;
production world rendering and its safe legacy fallback remain.

Provenance: `RAW_VERIFIED_COMMIT_RANGE`.

## 3. Rotation and Exact Baseline Synchronization

Pre-sync `20261009T162858883Z` ends at 32 matched samples with
translation max `6.58523447e-7 m` and rotation max `0 deg`; six start
mismatches were the separate baseline-resolution problem. Post-sync
`20261011T164644346Z` ends at 230, `1.308187e-6 m`, `0 deg`.
Its logged production and resolved timestamps agree. The later raw-GNSS run
also retains rotation evidence (`1.37674556e-6 m`, `0 deg`).

Keep exact baseline resolution/history and final-demo rotation diagnostics.

Provenance: `RAW_VERIFIED_COMMIT_RANGE`.

## 4. Raw Horizontal GNSS and Long-Lived Lifecycle

The raw long-lived captures are `20261012T052758539Z` and
`20261012T064147088Z`: respectively 165 and 294 world-GNSS records.
The first has 118 valid candidate records and observable
`candidateValidWhileProductionNone`, `timeInNoneMs`, source ages and
reacquisition errors. This supports observability beyond the legacy timeout,
not centimetre-grade accuracy.

The old reported long-outage aggregate (median `0.309 m`, p95 `0.549 m`,
better than held `30/33`, median improvement about `65%`) has not been
reproduced here from a complete, de-duplicated event set. It is not reassigned
to the unrelated October 11 capture or used as a new quantitative conclusion.

Provenance: `SUMMARY_ONLY` for that aggregate; raw lifecycle fields are verified.

## 5. GNSS+Velocity Complementary Experiment

The actual paired evidence is `20261012T153204510Z`: 122 world-GNSS
records, 69 fused-valid records and three distinct serialized endpoint pairs.
RAW/FUSED horizontal errors in metres are
`0.1353473/0.13506341`, `0.185237855/0.185510308`,
`0.5805696/0.5805656`. Their medians are
`0.185237855/0.185510308 m`; no practically meaningful endpoint gain was
demonstrated in this set.

Its 169 raw velocity callbacks have effective inter-first/last rate
`1.59731 Hz` and max gap `10.408988146 s`. This is the proper source of
the approximately 1.60 Hz / 10.41 s values, not `20261012T064147088Z`
(515 callbacks, approximately 1.83 Hz, max gap 21.82 s).
The older 32.6% fresh coverage / 5.30 s p95 age is summary-only until the
sampling population is explicitly reproduced.

See [algorithm notes](HISTORICAL_ALGORITHM_NOTES.md#gnssvelocity-complementary-experiment).

Provenance: `RAW_VERIFIED_COMMIT_RANGE`.

## 6. Reacquisition Investigation

`20261013T073405787Z` is the pre-fix long-lived production investigation;
`20261014T060059796Z` is the `9b15ceb` fix-lineage retest.
The latter logcat explicitly records pending and accepted
`mode=LONG_LIVED_TELEMETRY poseConsistency=SKIPPED` handovers with measured
dwell, including `334.2 ms`. Do not equate a HighConfidence snapshot with
an accepted authoritative visual pose.

The older post-NONE stale-reference mechanism is source-history evidence;
the later raw sessions cannot retroactively demonstrate the pre-reset policy.
See [algorithm notes](HISTORICAL_ALGORITHM_NOTES.md#historical-reacquisition-investigation).

Provenance: `RAW_VERIFIED_COMMIT_RANGE` for the long-lived retest.

## 7. Vertical Source Selection, Not Production

The two October 14 evening sessions have respectively 826 and 936 logged
`AircraftLocation3D` source samples, with median source intervals
`213.873577 ms` and `214.871923 ms`. They contain 17 and 20 distinct
visual endpoint pairs. De-duplicate repeated `verticalDiagnostics` by
visual baseline/reacquisition timestamp, not by logger-record count.

Restricting to `abs(visualDeltaDownM)>=0.20 m` with both Location3D endpoint
ages `<=300 ms` gives 7 and 8 pairs; all 15 agree with positive-up altitude.
Positive-up residual medians are `0.03102237 m` and `0.052459121 m`
respectively (do not pool phases into one experiment statistic).
The second phase contains the documented `+0.30 m` altitude /
`-0.2682859 m` visual Down pair, with positive-up residual
`0.03171411 m` versus positive-down `0.568285942 m`.

`KeyAltitude` matches endpoint values but is often seconds old.
`RelateToTakeOffPosition` is absent. Both sessions replay the same two
old sea-level-height samples with `quality=0`; they are not four fresh
callbacks. These observations support selecting
`AircraftLocation3D.altitude`, positive-up, for this hardware/data set.
They do not establish a universal GNSS vertical datum.

See [algorithm notes](HISTORICAL_ALGORITHM_NOTES.md#vertical-source-selection-experiment).

Provenance: `RAW_VERIFIED_COMMIT_RANGE`.

## 8. Production Vertical and Horizontal Limitation

`20261015T054613552Z` actually contains `verticalProductionSource`,
`verticalLocalizationState`, raw/smoothed Down deltas and production vertical
configuration. It also contains the satellite trust admission fields.
The source is Location3D altitude, freshness `300 ms`, `tau=0.4 s`,
`deltaDown=-deltaAltitude`; the equations are confirmed in `82636b5`.
No end-to-end production vertical p95 is newly asserted here.

The reported stationary horizontal drifts of approximately 0.93 m / 0.49 m
remain physical-test interpretations, not newly derived stationarity truth.
The distinction between code configuration and physical accuracy is retained.

Provenance: `RAW_VERIFIED_COMMIT_RANGE` for production field presence.

## 9. Horizontal Quality-Signal Negative Result

`20261016T115100463Z` contains two interference callback records and no
fusion-positioning callback record. No fresh usable quality veto is demonstrated.
Zero callbacks alone must not be rewritten as proof of unsupported hardware.
This does not validate satellite count as sufficient or velocity as production
input. Keep vertical independent from horizontal quality admission.

Provenance: `RAW_VERIFIED_COMMIT_RANGE`.

## Retirement Decision After Documentation Extraction

These decisions cover only historical diagnostic implementations, not production
algorithms, source histories, authoritative state, active quality gates or tests.

| Group | Evidence archived | Algorithm documented | New physical test needed for retirement | Eligible for scoped retirement |
| --- | --- | --- | --- | --- |
| GNSS+velocity complementary diagnostics | YES; actual A/B at 20261012T153204510Z | YES | NO | YES |
| Vertical source-selection diagnostics | YES; both October 14 phases | YES | NO | YES |
| Historical reacquisition comparison/scoring diagnostics | YES; source history plus pre/post long-lived captures | YES | NO | YES, diagnostic scoring/output only |

No code is retired by this documentation step. Preserve short-window production
pose agreement, fresh/long-lived quality+dwell admission, provisional-candidate
baseline protection, exact baseline history, vertical production and all final
source fallbacks. Raw GNSS, production smoothing, quality and timing diagnostics
are outside this retirement decision.

## Remaining Thesis Work

RoofCanopy board-local placement, `T_board_model`, final scale/orientation/offset
and their physical validation remain separate unfinished work. This repair
does not change those claims or authorize further cleanup.
