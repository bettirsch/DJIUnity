# DroneView logging

## Modes and location

NORMAL is the default. Each DroneView session writes one
`dji-session-events-<sessionId>.jsonl` file with lifecycle events, render-source
changes (including visual loss and reacquisition), changes in telemetry
availability/health, and Unity errors, exceptions, and assertions. Telemetry
health is sampled every 500 ms by default and written only when it changes.
It is not a per-frame trace. Android/DJI errors can also appear separately in
Android logcat. The SDK's own logging is not controlled by this setting.

To make a diagnostic build, set `DefaultLoggingMode` in
`DjiTelemetryValidationLogger.cs` to `DjiLoggingMode.Diagnostic`, or call
`ConfigureNextSessionLogging(DjiLoggingMode.Diagnostic)` from code before the
logger starts a session. A request during an open session applies to that
logger's next session, not to the current one. There is no UI or saved preference.
Diagnostic experiment build defines do not themselves enable logging.

DIAGNOSTIC retains the existing JSONL record schemas and names:

| File prefix | Typical contents |
| --- | --- |
| `dji-telemetry-validation` | Periodic telemetry and quality callbacks |
| `dji-visual-latency` | Vision, orientation, propagation and writer health |
| `dji-outage-events` | Outage and visual reacquisition comparisons |
| `dji-position-callbacks` | Raw Android GNSS callbacks |
| `dji-velocity-callbacks` | Raw Android velocity callbacks |

The five diagnostic files use the same UTC session ID
(`yyyyMMddTHHmmssfffZ`). The last two files require the Android bridge.
On Android the normal location is
`/storage/emulated/0/Android/data/com.sok9hu/files/`.
Export any flight to keep before the next session or before closing the current
one if it has already exceeded the budget. A session exported outside the
application's log directory is not managed by rotation.

## Retention and limits

At session start and after every orderly session close, a background worker
scans only the six recognized JSONL file names in the application's persistent
root, grouping files by their exact session IDs. It attempts to delete closed
sessions started more than 14 days ago, then deletes the oldest remaining
closed groups until the recognized files fit within 100 MB (100,000,000 bytes).
It does not traverse subdirectories or manage exported flight data, optional
detector capture images, screenshots, or other application data.

Active and draining sessions are protected, even when their files alone exceed
100 MB. The quota is not a continuous hard limit; a single large session may
be deleted after it closes. Storage errors are best effort and logged rather
than retried on the render thread. If the app is force-killed, buffered records
may be lost and rotation is retried on the next DroneView start.

## Work done on each thread

Before this change, Unity opened and closed the files on its main thread,
serialized the records there, and wrote/flushed the telemetry and outage files
there. Only the visual writer and raw Android callback writer already had workers.
Now all six output types use bounded asynchronous writers. A separate lifecycle
worker handles scans, deletions and waits for file closure. The Android bridge
keeps its 1024-record raw queue; each managed stream has a 256-record queue.
Queue-full drops do not stall pose processing. Diagnostic schemas, including the
visual writer's health report, remain unchanged.

NORMAL skips detailed serialization and the guarded vision traces. DIAGNOSTIC
still constructs and serializes managed records on the Unity thread and builds
the raw callback record before enqueueing it. Those costs are intentional, and
file throughput and loss should be checked on Android before diagnostic flights.
Neither log mode changes the camera pose, model, telemetry inputs or their gates.

## Validation

- Unity 6000.0.72f1: 818/818 EditMode tests passed, including six new logging
  tests. Android C# compiled with the normal configuration and with the
  diagnostic experiment defines; this was not an APK build.
- The logging-only build passed 38/38 DJIBridge release unit tests. Its AAR
  SHA-256 was `B29D4ED16E004873098C33565E8767B5DB77435B97FE0B1DB2715CD3E19A44CA`.
- Final validation with the fixed FC3582 camera model: 828/828 Unity EditMode
  tests and 38/38 DJIBridge release unit tests passed, with no skips. The AAR
  used by Unity matches the final DJIBridge output (SHA-256:
  `5F7257A1E1B6CFD730DB3A6661AAF3C7FA9EC2037AA2547A3358FB5E5AD51D86`).
- In an EditMode microbenchmark, 2,000 calls for one representative visual
  record took 0.0164 ms with detailed logging off (no record produced) and
  46.2140 ms with it on (serialization into a StringWriter, not disk). The
  latter emitted 3,498,000 UTF-8 bytes for this one record type; at an assumed
  30 records/s that would be about 3.15 MB/min. This is not the actual flight
  rate. A simulated NORMAL session with seven events produced a 1,105-byte file.
- A blocked sink test held one record in flight, filled both queue slots, and
  dropped the remaining 98 submissions; the producer and the close request
  completed without waiting for the sink. The Editor's allocation counter did
  not register the control allocation, so no allocation total is reported.
- The historical 4 min 57 s flight used approximately 33.42 MB (6.75 MB/min)
  across all five streams. Android FPS, render latency, sustained queue occupancy
  and real NORMAL file growth were not measured for this change.

## Changed files

- Unity session policy/storage: `DjiTelemetryValidationLogger.cs`,
  `BoundedDiagnosticJsonlWriter.cs`, `DjiLogRetention.cs`, `DJIPoseProvider.cs`.
- Unity diagnostic producers: `DjiBoardVisionProvider.cs`,
  `DjiBoardOrientationDiagnostics.cs`, `DjiCameraPoseProvider.cs`,
  `DjiTelemetryMotionDiagnostics.cs`, `DjiRelativePositionExperimentDiagnostics.cs`,
  `DjiAprilTagBorderRetentionExperiment.cs`, `DjiAprilTagStaleJumpExperiment.cs`,
  `DjiAprilTagNoMarkersQualityLossExperiment.cs`.
- Bridge diagnostics: `DjiBoardVisionBridge.kt`, `DjiBoardVisionNative.kt`,
  `DjiRawTelemetryCallbackLogger.kt`, `dji_board_vision.cpp`, and the rebuilt
  Unity plugin `Assets/Plugins/Android/DJIUnityBridge.aar`.
- Validation: `DjiLoggingPolicyTests.cs`, this document, and Unity metadata for
  the new script and test.
