# Production horizontal GNSS configuration

Approved responsiveness update, 2026-11-20. The canonical defaults are in
`HorizontalGnssSmoothingEstimator.cs`: 0.04 m stationary noise envelope,
0.25 s exponential time constant, two direction-consistent distinct source
samples, and maximum 2.5 m/s output correction speed. This replaces the
previous 0.08 m / 0.5 s defaults without a new feature flag or estimator.

`DjiWorldLocalizationShadowCoordinator` constructs the existing estimator with
these defaults. There is no serialized production override. Targets strictly
below 0.04 m remain inside the noise envelope; the boundary is eligible for
the unchanged two-sample confirmation. Repeated polling cannot count twice.

The SAT23/21 gate, three-callback quality confirmation, 300 ms GNSS/altitude
freshness, 100 ms telemetry polling and all timestamp/history behavior remain
unchanged. RAW altitude, orientation, VISUAL/PnP, the candidate's 6-degree
orientation protection and bounded bracket waiting/250 ms dwell are untouched.
The normal `CommandLineAndroidBuild.BuildApk` now retains all accepted
AprilTag/native multi-start and wall-shadow features without candidate defines;
see [final build configuration](FINAL_BUILD_CONFIGURATION.md).

The HUD is deliberately unchanged. It can still display TELEMETRY (LIMITED)
during normal target confirmation or noise suppression. Faster target admission
does not establish better physical GNSS accuracy: coherent stationary wander
between 4 and 8 cm can now reach the output. The velocity cap bounds correction
rate, not total displacement. Larger persistent GNSS errors can still accumulate.

Deterministic old/new comparisons run the same C# estimator before and after
the two constant changes, with identical synthetic and recorded input schedules.
The recorded replay uses the November 11 source/quality/freshness/evaluation
sequence and recorded initial baseline state. It is not physical ground truth
or a replacement for the next stationary-hover/start-stop validation flight.
Historical evidence documents retain their original 8 cm / 0.5 s descriptions.
