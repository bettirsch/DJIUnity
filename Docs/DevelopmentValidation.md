# Development Validation

Open the project with the Unity version pinned in
`ProjectSettings/ProjectVersion.txt`. Run the EditMode suite in
`Assembly-CSharp-Editor` using the Unity Test Runner. Local captures and test
outputs are not versioned.

Optional inputs for the extended regression:

| Environment variable | Local input |
| --- | --- |
| `DJI_MULTISTART_FIXTURE_DIR` | `selected-native-unity-fixture.json`, a native multi-start replay result |
| `DJI_NO_MARKERS_FIXTURE_DIR` | `oct29-fixture.json`, a physical visual/telemetry replay; use a working copy because the test writes a CSV alongside it |
| `DJI_VERTICAL_RAW_REPLAY_DIR` | `vertical-raw-replay-fixture.csv`, a two-session altitude replay |
| `DJI_CAPTURE_TEST_OUTPUT_DIR` | `jvm-exact-y.pgm` and `jvm-exact-y.jsonl` from the bridge JVM test run |

The recorded multi-start and no-markers tests are skipped when their fixture
directories are not configured. A configured but missing or invalid fixture
still fails the corresponding test. The altitude and capture suites fall back
to deterministic inputs if no replay directory is configured.

Exact detector capture is for AprilTag/PnP replay, not camera measurement.
In a diagnostic Android build with `DJI_EXACT_DETECTOR_CAPTURE` and
`DjiLoggingMode.Diagnostic`, use the existing one-shot
`ARM_ONCE <nonce> <count>` token in `dji-calibration/arm.once`. The historical
directory name is kept for compatibility with existing captures. The saved
PGM bytes and manifest are the actual detector input and pose evidence.

In DJIBridge, run `gradlew.bat testReleaseUnitTest assembleRelease`.
The normal build produces `build/outputs/aar/DJIUnityBridge.aar` with the
multi-start experiment disabled. Use that file as the single Unity Android
bridge plugin; it includes the native OES integration. Experimental builds
must not replace the normal tracked plugin.

The bridge AAR keeps the manifest placeholder `DJI_API_KEY`. Supply the
value only when packaging the Unity Android application, using a local build
environment variable or Gradle user property.
