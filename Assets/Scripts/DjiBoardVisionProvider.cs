using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// DJI-only visual localization against the physical ReferenceBoardDefinition.
/// It consumes a separate ImageReader CPU stream; the DJI OES render path is
/// left untouched. All matrices use the parentFromChild convention.
/// </summary>
[DisallowMultipleComponent]
public sealed partial class DjiBoardVisionProvider : MonoBehaviour
{
    public enum LocalizationState
    {
        WaitingForReference,
        Localizing
    }

    public enum RotationPropagationMode
    {
        Disabled,
        ShadowDiagnostic,
        Enabled
    }

    public enum VisualPoseQuality
    {
        Unavailable,
        Degraded,
        HighConfidence
    }

    private enum VisualReacquisitionMode
    {
        None,
        PropagatedReacquisition,
        FreshRelocalization
    }

    [Header("Initialization Quality Gates")]
    [SerializeField, Min(1)] private int minimumVisibleMarkers = 2;
    [SerializeField, Min(4)] private int minimumCorners = 8;
    [SerializeField, Min(1)] private int consistentSamplesRequired = 5;
    [SerializeField, Min(0.1f)] private float maximumReprojectionRmsPixels = 4f;
    [SerializeField, Min(0.1f)] private float maximumResidualPixels = 8f;
    [SerializeField, Min(0.01f)] private float maximumSamplePositionDeltaMeters = 0.08f;
    [SerializeField, Range(1f, 90f)] private float maximumSampleRotationDeltaDegrees = 10f;

    [Header("Diagnostics")]
    [SerializeField] private bool diagnosticLogging = true;
    [SerializeField, Min(0.1f)] private float diagnosticLogIntervalSeconds = 1f;
    [SerializeField] private bool allowProvisionalCalibrationForValidation;

    [Header("Visual / Telemetry Rotation Diagnostics")]
    [Tooltip("Diagnostic only. Neither this comparison nor its offset scan changes rendering or board-loss behavior.")]
    [SerializeField] private bool compareVisualAndTelemetryRotation = true;
    [SerializeField, Min(10f)] private float maximumTelemetryBracketAgeMilliseconds = 250f;
    [SerializeField, Range(0.25f, 0.5f)] private float pendingTelemetryBracketTimeoutSeconds = 0.5f;
    [SerializeField, Min(1)] private int maximumPendingRotationDiagnosticSamples = 32;
    [SerializeField, Min(0f)] private float minimumRelativeRotationForOffsetScanDegrees = 1f;
    [SerializeField, Min(0.1f)] private float rotationComparisonStaleTimeoutSeconds = 2f;
    [SerializeField, Min(1f)] private float controlledTestStartDegrees = 2f;
    [SerializeField, Min(0.1f)] private float controlledTestCrossAxisLimitDegrees = 2f;
    [SerializeField, Min(0.01f)] private float stationaryAxisDeltaDegrees = 0.25f;
    [SerializeField, Min(0.1f)] private float yawCenterToleranceDegrees = 1f;
    [SerializeField, Min(1)] private int minimumYawLegPairedSamples = 20;

    [Header("Rotation-Only Propagation")]
    [Tooltip("Enabled renders the validated rotation-only prediction while a recent visual board pose is unavailable.")]
    [SerializeField] private RotationPropagationMode rotationPropagationMode = RotationPropagationMode.Enabled;
    [Tooltip("Developer-only override for unvalidated builds. Normal builds are enabled by the physical yaw-validation safety gate.")]
    [SerializeField] private bool allowUnvalidatedRotationPropagationForDeveloperTesting;
    [Tooltip("Conservative diagnostic limit. Rotation-only propagation has no camera translation.")]
    [SerializeField, Min(0.1f)] private float maxRotationOnlyPropagationSeconds = 2f;
    [SerializeField, Min(0.1f)] private float shadowPropagationLogIntervalSeconds = 1f;

    [Header("Visual Temporal Compensation")]
    [Tooltip("Render-only visual pose extrapolation using the current OES/CPU apparent timing horizon. It never changes visual localization, quality, outage baselines, or propagation.")]
    [SerializeField] private VisualTemporalCompensationMode visualTemporalCompensationMode = VisualTemporalCompensationMode.Disabled;
    [SerializeField] private VisualTemporalCompensationConfiguration visualTemporalCompensationConfiguration = new();

    [Header("Persistent World Visual Render A/B")]
    [Tooltip("VISUAL-only route. Persistent World is the validated default; Legacy remains available for A/B diagnostics and fallback.")]
    [SerializeField] private VisualRenderPath visualRenderPath = PersistentWorldVisualRenderPath.DefaultPath;

    [Header("Velocity Translation Shadow")]
    [Tooltip("Diagnostic only. Integrates KeyAircraftVelocity for comparison at board reacquisition; it never controls the rendered cube.")]
    [SerializeField] private bool enableVelocityTranslationShadow = true;
    [Tooltip("Loose diagnostic comparison only. Maximum age of the last event-driven AircraftVelocity state held by SHADOW_LOOSE. This does not affect the 2-second rendered rotation-only timeout.")]
    [SerializeField, Min(1f)] private float maximumVelocitySampleAgeForShadowMilliseconds = 2000f;
    [Tooltip("Trusted diagnostic policy. SHADOW_FRESH rejects velocity coverage older than this threshold and never controls rendering.")]
    [SerializeField, Min(1f)] private float maximumVelocityAgeForTrustedShadowMilliseconds = 500f;

    [Header("Horizontal GNSS Translation Shadow")]
    [Tooltip("Diagnostic only. Compares timestamped KeyAircraftLocation3D horizontal displacement during board loss; it never controls rendering.")]
    [SerializeField] private bool enableHorizontalGnssTranslationShadow = true;
    [Tooltip("Conservative physical-data cap for a causal GNSS sample used by SHADOW_HORIZONTAL_GNSS. The observed p95 was about 192 ms and maximum about 253 ms.")]
    [SerializeField, Min(1f)] private float maximumGnssPositionSampleAgeForShadowMilliseconds = 300f;

    [Header("Experimental Guarded Rendered Horizontal GNSS")]
    [Tooltip("Legacy horizontal GNSS experiment. The persistent-world telemetry render path is independent of this setting.")]
    [SerializeField] private HorizontalTranslationRenderMode horizontalTranslationRenderMode = HorizontalTranslationRenderMode.ShadowOnly;
    [SerializeField] private GuardedHorizontalRenderConfiguration guardedHorizontalRenderConfiguration = new();

    [Header("Hybrid Horizontal Position Shadow")]
    [Tooltip("Diagnostic only. Uses GNSS from the shared visual baseline plus only fresh velocity after the latest GNSS sample. It never controls rendering.")]
    [SerializeField] private bool enableHybridHorizontalPositionShadow = true;

    [Header("Aircraft Velocity Visual Validation")]
    [Tooltip("Diagnostic only. Compares visual NED camera displacement against the existing zero-order-hold AircraftVelocity integral while four-marker visual tracking remains continuous.")]
    [SerializeField] private bool enableAircraftVelocityVisualValidation = true;
    [Tooltip("Visual motion below this distance is excluded from velocity scale ratios to avoid dividing by ordinary board-pose noise.")]
    [SerializeField, Min(0.001f)] private float velocityVisualValidationMinimumDistanceMeters = 0.02f;
    [Tooltip("A prior high-confidence visual pose must be within this tolerance of the 250/500/1000 ms diagnostic window.")]
    [SerializeField, Min(1f)] private float velocityVisualValidationWindowToleranceMilliseconds = 100f;
    [Tooltip("Velocity distances at or below this value count as effectively zero for sparse-callback and lever-arm diagnostics.")]
    [SerializeField, Min(0.0001f)] private float velocityVisualValidationNearZeroVelocityDistanceMeters = 0.005f;
    [Tooltip("Per-axis signs are compared only when the visual component exceeds this distance.")]
    [SerializeField, Min(0.0001f)] private float velocityVisualValidationMinimumAxisDistanceMeters = 0.01f;
    [Tooltip("Combined raw aircraft-yaw or gimbal-pitch change at or below this value is classified as translation-dominant when visual motion is meaningful.")]
    [SerializeField, Min(0.01f)] private float velocityVisualValidationTranslationDominantRotationDegrees = 1f;
    [Tooltip("Combined raw aircraft-yaw or gimbal-pitch change at or above this value is classified as rotation-dominant when velocity remains near zero.")]
    [SerializeField, Min(0.01f)] private float velocityVisualValidationRotationDominantDegrees = 2f;

    [Header("Aircraft Position Visual Validation")]
    [Tooltip("Diagnostic-only KeyAircraftLocation3D versus high-confidence visual-board comparison. It never affects rendering or the rotation/velocity shadows.")]
    [SerializeField] private bool enableAircraftPositionVisualValidation = true;
    [SerializeField, Min(100f)] private float maximumAircraftPositionSampleAgeMilliseconds = 5000f;
    [SerializeField, Min(0.01f)] private float positionVisualValidationMinimumDistanceMeters = 0.02f;

    [Header("Visual Render Quality")]
    [Tooltip("The final pre-loss physical sample was within 31 px of the image edge. This margin prevents edge-clipped poses from moving rendered content.")]
    [SerializeField, Min(1f)] private float highConfidenceMinimumBorderDistancePixels = 64f;
    [Tooltip("Conservative RMS limit for one to three markers. Four-marker poses use their dedicated limit below.")]
    [SerializeField, Min(0.1f)] private float highConfidenceMaximumReprojectionRmsPixels = 2f;
    [SerializeField, Min(0.1f)] private float highConfidenceMaximumCornerResidualPixels = 4f;
    [Tooltip("Four-marker reprojection quality is normalized by the median observed marker edge length. The 0.065 limit covers the stable 4-7 px physical residuals measured across roughly 75-155 px marker scales.")]
    [SerializeField, Min(0.0001f)] private float highConfidenceFourMarkerMaximumNormalizedRms = 0.065f;
    [Tooltip("Four-marker worst-corner quality is normalized by the median observed marker edge length. One to three markers retain the fixed 4 px limit above.")]
    [SerializeField, Min(0.0001f)] private float highConfidenceFourMarkerMaximumNormalizedCornerResidual = 0.10f;
    [Tooltip("Only 0.1005-0.1020 normalized corner residuals were observed at the valid threshold. Values through 0.105 are held briefly while VISUAL authority is already established; this does not change the 0.100 pass threshold.")]
    [SerializeField, Min(0.0001f)] private float highConfidenceFourMarkerBorderlineNormalizedCornerResidual = 0.105f;
    [Tooltip("Four-marker border distances from 52-63 px were observed as marginal. Distances from 48 px through the normal 64 px threshold are held briefly; closer poses remain severe.")]
    [SerializeField, Min(1f)] private float highConfidenceFourMarkerSevereBorderDistancePixels = 48f;
    [Tooltip("Consecutive borderline four-marker failures required before VISUAL authority yields to propagation.")]
    [SerializeField, Min(2)] private int fourMarkerBorderlineQualityConfirmationFrames = 3;
    [Tooltip("Emergency outlier cap only. It is deliberately much larger than ordinary close-range four-marker residuals.")]
    [SerializeField, Min(1f)] private float highConfidenceFourMarkerHardMaximumReprojectionRmsPixels = 25f;
    [Tooltip("Emergency outlier cap only. It catches pathological 40-60 px corner residuals without rejecting ordinary close-range poses.")]
    [SerializeField, Min(1f)] private float highConfidenceFourMarkerHardMaximumCornerResidualPixels = 35f;
    [Tooltip("One-to-three marker poses retain this immediate camera-space translation-jump limit. Four-marker poses use the motion-aware policy below.")]
    [SerializeField, Min(0.001f)] private float highConfidenceMaximumFrameTranslationJumpMeters = 0.035f;
    [Tooltip("Four-marker translation changes beyond the normal limit are held briefly while telemetry rotation and diagnostic velocity determine whether the motion is coherent.")]
    [SerializeField, Min(1)] private int motionAwareTranslationJumpConfirmationFrames = 3;
    [Tooltip("A four-marker camera-space jump at or above this size is rejected immediately rather than held as a borderline motion candidate.")]
    [SerializeField, Min(0.01f)] private float motionAwareImmediateTranslationJumpRejectMeters = 0.15f;
    [Tooltip("Residual tolerance after compensating the measured telemetry rotation. This is not a rendered translation correction.")]
    [SerializeField, Min(0f)] private float motionAwareTranslationResidualAllowanceMeters = 0.015f;
    [SerializeField, Min(1f)] private float motionAwareTelemetrySpeedAllowanceMultiplier = 1.5f;
    [SerializeField, Range(0.1f, 90f)] private float highConfidenceMaximumFrameRotationJumpDegrees = 10f;
    [SerializeField] private bool allowSingleMarkerHighConfidence = true;
    [SerializeField, Min(1)] private int highConfidenceReacquisitionConfirmationFrames = 3;

    [Header("Visual Reacquisition Handover")]
    [Tooltip("After visual quality returns, keep rendering the propagated pose for this dwell before visual authority can return.")]
    [SerializeField, Min(0.01f)] private float reacquisitionStableTimeSeconds = 0.25f;
    [Tooltip("The recent outdoor reacquisition median was about 0.49 m with an unmodeled rotation-only translation. This conservative guard catches only large visual/predicted disagreements; it does not compensate translation.")]
    [SerializeField, Min(0.01f)] private float reacquisitionMaximumPositionDifferenceMeters = 1f;
    [Tooltip("The recent <=2 s rotation-only reacquisition p95 was below 4.5 degrees. This guard leaves margin for normal telemetry/vision timing error.")]
    [SerializeField, Range(0.1f, 90f)] private float reacquisitionMaximumRotationDifferenceDegrees = 6f;
    // Latest stationary-GNSS measurements showed roughly 14-33 mm of
    // short-window horizontal noise. This diagnostic-only neutral band avoids
    // overinterpreting prediction differences at that scale.
    private const float ReacquisitionHorizontalNeutralMarginMeters = 0.03f;

    [Header("Physical Validation Buckets")]
    [SerializeField, Min(30)] private int minimumSamplesPerValidationBucket = 30;
    [SerializeField, Min(0.01f)] private float nearValidationDistanceMeters = 1f;
    [SerializeField, Min(0.01f)] private float farValidationDistanceMeters = 2f;
    [SerializeField, Range(1f, 89f)] private float moderateTiltDegrees = 15f;
    [SerializeField, Min(1)] private int stationarySettleSamples = 10;
    [SerializeField, Min(0.0001f)] private float stationaryFramePositionDeltaMeters = 0.01f;
    [SerializeField, Min(0.01f)] private float stationaryFrameRotationDeltaDegrees = 1f;

    public LocalizationState State { get; private set; } = LocalizationState.WaitingForReference;
    // Raw native optical-frame pose, T_cameraCv_board. It remains separate
    // from Unity's rendering view basis; DjiSharedReferenceCubeOverlay owns
    // the one explicit OpenCV-to-Unity view conversion.
    public bool HasAcceptedCameraFromBoard { get; private set; }
    public Pose CurrentCameraFromBoard { get; private set; } = Pose.identity;
    public float AcceptedCameraFromBoardAgeSeconds => HasAcceptedCameraFromBoard
        ? Mathf.Max(0f, Time.realtimeSinceStartup - _lastAcceptedCameraFromBoardRealtime)
        : float.PositiveInfinity;
    public VisualPoseQuality CurrentVisualPoseQuality { get; private set; } = VisualPoseQuality.Unavailable;
    public bool HasHighConfidenceCameraFromBoard { get; private set; }
    public Pose CurrentHighConfidenceCameraFromBoard { get; private set; } = Pose.identity;
    public float HighConfidenceCameraFromBoardAgeSeconds => HasHighConfidenceCameraFromBoard
        ? Mathf.Max(0f, Time.realtimeSinceStartup - _lastHighConfidenceCameraFromBoardRealtime)
        : float.PositiveInfinity;
    // Raised only after a high-confidence visual pose becomes authoritative.
    // Consumers must treat this as observational; it does not participate in
    // the existing render-source decision.
    public event Action<Pose, long> AuthoritativeVisualPoseAccepted;
    public NativeBoardResult LatestResult { get; private set; }
    public DjiCameraCalibration Calibration => _calibration;
    public RotationPropagationMode CurrentRotationPropagationMode => rotationPropagationMode;
    public HorizontalTranslationRenderMode CurrentHorizontalTranslationRenderMode => horizontalTranslationRenderMode;
    public int HorizontalModeSegmentId => _horizontalModeSegmentId;
    public VisualTemporalCompensationMode CurrentVisualTemporalCompensationMode => visualTemporalCompensationMode;
    public int VisualTemporalCompensationSegmentId => _visualTemporalCompensationSegmentId;
    public VisualRenderPath CurrentVisualRenderPath => visualRenderPath;
    public int VisualRenderPathSegmentId => _visualRenderPathSegmentId;
    public VisualRenderPathDiagnostics LatestVisualRenderPathDiagnostics => _latestVisualRenderPathDiagnostics;
    public bool HasTelemetryOrientation => _telemetryProvider != null && _telemetryProvider.HasTelemetryOrientation;
    public bool HasTelemetryPosition => _telemetryProvider != null && _telemetryProvider.HasTelemetryPosition;
    public DjiRenderPoseSource CurrentRenderPoseSource { get; private set; } = DjiRenderPoseSource.None;
    public float BoardHiddenAgeSeconds => PropagationAgeSeconds;

    internal string GetTelemetryPropagationDiagnosticPhase(DjiRenderPoseSource source, float ageSeconds) =>
        source != DjiRenderPoseSource.TelemetryPropagated ? null :
        IsWithinShortPropagationWindow(ageSeconds) ? "SHORT_WINDOW" : "LONG_LIVED";
    public bool TryGetShadowPredictedCameraFromBoard(out Pose cameraFromBoard)
    {
        cameraFromBoard = _latestShadowPredictedCameraFromBoard;
        return _hasLatestShadowPrediction;
    }

    public readonly struct HybridPositionShadowSnapshot
    {
        public readonly ReferenceFrameTransforms.HybridHorizontalShadowMode mode;
        public readonly Vector3 navigationDisplacement;
        public readonly long predictionTimestampNs;
        public readonly long latestGnssTimestampNs;
        public readonly long velocityStartTimestampNs;
        public readonly long velocityEndTimestampNs;
        public readonly double gnssAgeMilliseconds;
        public readonly double velocityAgeMilliseconds;
        public readonly bool velocityCoverageValid;
        public readonly string reason;

        public HybridPositionShadowSnapshot(
            ReferenceFrameTransforms.HybridHorizontalShadowMode mode,
            Vector3 navigationDisplacement,
            long predictionTimestampNs,
            long latestGnssTimestampNs,
            long velocityStartTimestampNs,
            long velocityEndTimestampNs,
            double gnssAgeMilliseconds,
            double velocityAgeMilliseconds,
            bool velocityCoverageValid,
            string reason)
        {
            this.mode = mode;
            this.navigationDisplacement = navigationDisplacement;
            this.predictionTimestampNs = predictionTimestampNs;
            this.latestGnssTimestampNs = latestGnssTimestampNs;
            this.velocityStartTimestampNs = velocityStartTimestampNs;
            this.velocityEndTimestampNs = velocityEndTimestampNs;
            this.gnssAgeMilliseconds = gnssAgeMilliseconds;
            this.velocityAgeMilliseconds = velocityAgeMilliseconds;
            this.velocityCoverageValid = velocityCoverageValid;
            this.reason = reason;
        }
    }

    public readonly struct GuardedHorizontalRenderSnapshot
    {
        public readonly HorizontalTranslationRenderMode renderMode;
        public readonly GuardedHorizontalTranslationState state;
        public readonly bool eligible;
        public readonly bool active;
        public readonly Vector2 correctionInNavigation;
        public readonly Vector2 appliedCorrectionInNavigation;
        public readonly float blendAlpha;
        public readonly float gnssAgeMilliseconds;
        public readonly float propagationAgeMilliseconds;
        public readonly int consistentSampleCount;
        public readonly float directionDifferenceDegrees;
        public readonly float armingAgeMilliseconds;
        public readonly float armingTimeoutMilliseconds;
        public readonly long firstQualifyingSampleTimestampNs;
        public readonly long lastDistinctGnssTimestampNs;
        public readonly float enableThresholdMeters;
        public readonly float disableThresholdMeters;
        public readonly string reason;

        public GuardedHorizontalRenderSnapshot(
            HorizontalTranslationRenderMode renderMode,
            GuardedHorizontalStateSnapshot snapshot,
            GuardedHorizontalRenderConfiguration configuration)
        {
            this.renderMode = renderMode;
            state = snapshot.state;
            eligible = snapshot.eligible;
            active = snapshot.active;
            correctionInNavigation = snapshot.correctionInNavigation;
            appliedCorrectionInNavigation = snapshot.appliedCorrectionInNavigation;
            blendAlpha = snapshot.blendAlpha;
            gnssAgeMilliseconds = snapshot.gnssAgeMilliseconds;
            propagationAgeMilliseconds = snapshot.propagationAgeMilliseconds;
            consistentSampleCount = snapshot.consistentSampleCount;
            directionDifferenceDegrees = snapshot.directionDifferenceDegrees;
            armingAgeMilliseconds = snapshot.armingAgeMilliseconds;
            armingTimeoutMilliseconds = snapshot.armingTimeoutMilliseconds;
            firstQualifyingSampleTimestampNs = snapshot.firstQualifyingSampleTimestampNs;
            lastDistinctGnssTimestampNs = snapshot.lastDistinctGnssTimestampNs;
            enableThresholdMeters = configuration.horizontalEnableThresholdMeters;
            disableThresholdMeters = configuration.horizontalDisableThresholdMeters;
            reason = snapshot.reason;
        }
    }

    public bool TryGetLatestHybridPositionShadow(out HybridPositionShadowSnapshot snapshot)
    {
        snapshot = _latestHybridPositionShadow;
        return _hasLatestHybridPositionShadow;
    }

    public bool TryGetGuardedHorizontalRenderSnapshot(out GuardedHorizontalRenderSnapshot snapshot)
    {
        snapshot = _latestGuardedHorizontalRenderSnapshot;
        return _hasLatestGuardedHorizontalRenderSnapshot;
    }

    /// <summary>
    /// Supplies the read-only persistent-world shadow used only by the opt-in
    /// VISUAL render path. Replacing this reference never resets any state.
    /// </summary>
    public void ConfigureWorldLocalizationShadow(DjiWorldLocalizationShadow worldLocalizationShadow) =>
        _worldLocalizationShadow = worldLocalizationShadow;

    /// <summary>
    /// Changes the authoritative experimental horizontal render mode. The
    /// visual pose and rotation baseline remain intact; only the guarded GNSS
    /// contribution is reset so no correction crosses an A/B boundary.
    /// </summary>
    public bool SetHorizontalTranslationRenderMode(HorizontalTranslationRenderMode mode)
    {
        if (mode == horizontalTranslationRenderMode)
            return false;

        var previousMode = horizontalTranslationRenderMode;
        var timestampNs = DjiBoardVisionBridge.GetMonotonicTimestampNs();
        _outageVisualGroundTruth.MarkModeChangedDuringOutage();
        horizontalTranslationRenderMode = mode;
        _horizontalModeSegmentId++;
        ResetGuardedHorizontalRenderState("HORIZONTAL_RENDER_MODE_CHANGED", timestampNs);
        Debug.Log(
            "DJI_HORIZONTAL_RENDER_MODE_CHANGED " +
            $"from={previousMode} to={horizontalTranslationRenderMode} timestampNs={timestampNs} " +
            $"horizontalModeSegmentId={_horizontalModeSegmentId}");
        return true;
    }

    public bool ToggleHorizontalTranslationRenderMode()
    {
        var nextMode = horizontalTranslationRenderMode == HorizontalTranslationRenderMode.GuardedGnssRendered
            ? HorizontalTranslationRenderMode.ShadowOnly
            : HorizontalTranslationRenderMode.GuardedGnssRendered;
        return SetHorizontalTranslationRenderMode(nextMode);
    }

    /// <summary>
    /// Changes only the render-side temporal compensation experiment. Raw
    /// visual localization and all outage/GNSS policies intentionally persist.
    /// </summary>
    public bool SetVisualTemporalCompensationMode(VisualTemporalCompensationMode mode)
    {
        if (mode == visualTemporalCompensationMode)
            return false;

        var previousMode = visualTemporalCompensationMode;
        visualTemporalCompensationMode = mode;
        _visualTemporalCompensationSegmentId++;
        ResetVisualTemporalCompensationPredictor("TEMPORAL_MODE_CHANGED");
        Debug.Log(
            "DJI_VISUAL_TEMPORAL_COMPENSATION_MODE_CHANGED " +
            $"from={previousMode} to={visualTemporalCompensationMode} " +
            $"temporalSegmentId={_visualTemporalCompensationSegmentId}");
        return true;
    }

    public bool ToggleVisualTemporalCompensationMode()
    {
        var nextMode = visualTemporalCompensationMode == VisualTemporalCompensationMode.Enabled
            ? VisualTemporalCompensationMode.Disabled
            : VisualTemporalCompensationMode.Enabled;
        return SetVisualTemporalCompensationMode(nextMode);
    }

    /// <summary>
    /// Changes only the raw VISUAL render route. Localization, temporal
    /// prediction, propagation, GNSS, and shared content deliberately persist.
    /// </summary>
    public bool SetVisualRenderPath(VisualRenderPath path)
    {
        if (path == visualRenderPath)
            return false;

        var previousPath = visualRenderPath;
        visualRenderPath = path;
        _visualRenderPathSegmentId++;
        Debug.Log(
            "DJI_VISUAL_RENDER_PATH_CHANGED " +
            $"from={previousPath} to={visualRenderPath} " +
            $"visualRenderPathSegmentId={_visualRenderPathSegmentId}");
        return true;
    }

    public bool ToggleVisualRenderPath() =>
        SetVisualRenderPath(
            visualRenderPath == VisualRenderPath.LegacyBoardPath
                ? VisualRenderPath.PersistentWorldPath
                : VisualRenderPath.LegacyBoardPath);

    /// <summary>
    /// Applies the non-interactive DroneView startup policy before any visual
    /// localization, propagation, or render routing consumes these modes.
    /// The development APIs remain available; DroneView simply no longer
    /// exposes their A/B controls to normal users.
    /// </summary>
    public void ApplyDroneViewStartupDefaults()
    {
        rotationPropagationMode = RotationPropagationMode.Enabled;
        horizontalTranslationRenderMode = HorizontalTranslationRenderMode.ShadowOnly;
        _horizontalModeSegmentId = 0;
        visualTemporalCompensationMode = VisualTemporalCompensationMode.Disabled;
        _visualTemporalCompensationSegmentId = 0;
        visualRenderPath = PersistentWorldVisualRenderPath.DefaultPath;
        _visualRenderPathSegmentId = 0;
        _worldRenderFallbackCount = 0;
        _lastWorldRenderFallbackSourceTimestampNs = RotationPropagationTiming.NoTimestamp;
        ResetVisualTemporalCompensationPredictor("DRONE_VIEW_STARTUP_DEFAULTS");
    }

    /// <summary>
    /// Records the moment the shared overlay actually applies the current
    /// authoritative visual pose. This is diagnostic only and never changes
    /// source selection, prediction, or rendering.
    /// </summary>
    public void RecordVisualPoseAppliedForRender(Pose? appliedPose = null)
    {
#if DJI_APRILTAG_BORDER_RETENTION_EXPERIMENT
        if (appliedPose.HasValue) TraceBorderRetentionVisualApplication(appliedPose.Value);
#endif
#if DJI_APRILTAG_STALE_JUMP_EXPERIMENT
        if (appliedPose.HasValue) TraceStaleJumpAppliedPose(appliedPose.Value);
#endif
        if (appliedPose.HasValue)
            _telemetryValidationLogger?.RecordMotionPoseApplied(
                DjiBoardVisionBridge.GetMonotonicTimestampNs(), CurrentRenderPoseSource, appliedPose.Value);
        if (CurrentRenderPoseSource != DjiRenderPoseSource.Visual ||
            !_hasAuthoritativeVisualTimingSample ||
            _telemetryValidationLogger == null)
        {
            return;
        }

        var appliedTimestampNs = DjiBoardVisionBridge.GetMonotonicTimestampNs();
        DJIGPUBackground.TryGetLatestDisplayedFrameTiming(out var displayedFrameTiming);
        _visualTemporalCompensationPredictor.RecordApplicationTimestamp(
            _authoritativeVisualTimingSample.sourceFrameId,
            appliedTimestampNs);
        _telemetryValidationLogger.WriteVisualLatencySample(
            _authoritativeVisualTimingSample,
            appliedTimestampNs,
            displayedFrameTiming,
            _latestVisualTemporalCompensationSnapshot,
            _latestVisualRenderPathDiagnostics);
    }

    /// <summary>
    /// Resolves the visual overlay pose only. CurrentHighConfidenceCameraFromBoard
    /// remains raw and is still the sole pose used by quality, reacquisition,
    /// propagation baselines, and outage ground truth.
    /// </summary>
    private Pose ResolveVisualTemporalCompensatedRenderPose(Pose rawVisualPose)
    {
        if (visualTemporalCompensationMode != VisualTemporalCompensationMode.Enabled ||
            !_hasAuthoritativeVisualTimingSample)
        {
            _latestVisualTemporalCompensationSnapshot = VisualTemporalCompensationSnapshot.Uncompensated(
                visualTemporalCompensationMode,
                _visualTemporalCompensationSegmentId,
                rawVisualPose,
                visualTemporalCompensationMode == VisualTemporalCompensationMode.Enabled
                    ? "VISUAL_TIMING_UNAVAILABLE"
                    : "MODE_DISABLED");
            return rawVisualPose;
        }

        if (!DJIGPUBackground.TryGetLatestDisplayedFrameTiming(out var displayedFrameTiming))
        {
            _latestVisualTemporalCompensationSnapshot = VisualTemporalCompensationSnapshot.Uncompensated(
                visualTemporalCompensationMode,
                _visualTemporalCompensationSegmentId,
                rawVisualPose,
                "OES_TIMING_UNAVAILABLE");
            return rawVisualPose;
        }

        var displayedTimestampNs = displayedFrameTiming.HasFrameAvailableTimestamp
            ? displayedFrameTiming.oesFrameAvailableTimestampNs
            : displayedFrameTiming.HasLatchTimestamp
                ? displayedFrameTiming.oesLatchTimestampNs
                : 0L;
        var nowTimestampNs = DjiBoardVisionBridge.GetMonotonicTimestampNs();
        if (_visualTemporalCompensationPredictor.TryPredict(
                _authoritativeVisualTimingSample.sourceFrameId,
                displayedTimestampNs,
                nowTimestampNs,
                visualTemporalCompensationConfiguration,
                out var predictedPose,
                out var horizonMilliseconds,
                out var linearVelocity,
                out var angularVelocityDegreesPerSecond,
                out var reason))
        {
            _latestVisualTemporalCompensationSnapshot = new VisualTemporalCompensationSnapshot(
                visualTemporalCompensationMode,
                _visualTemporalCompensationSegmentId,
                true,
                horizonMilliseconds,
                linearVelocity,
                angularVelocityDegreesPerSecond,
                rawVisualPose,
                predictedPose,
                "NONE");
            return predictedPose;
        }

        _latestVisualTemporalCompensationSnapshot = VisualTemporalCompensationSnapshot.Uncompensated(
            visualTemporalCompensationMode,
            _visualTemporalCompensationSegmentId,
            rawVisualPose,
            reason);
        return rawVisualPose;
    }

    private Pose ResolveVisualRawRenderPose()
    {
        _worldLocalizationShadow ??= FindFirstObjectByType<DjiWorldLocalizationShadow>();
        var snapshot = _worldLocalizationShadow != null
            ? _worldLocalizationShadow.Snapshot
            : default;
        var resolution = PersistentWorldVisualRenderPath.Resolve(
            visualRenderPath,
            CurrentHighConfidenceCameraFromBoard,
            _currentHighConfidenceVisualTimestampNs,
            snapshot);
        if (visualRenderPath == VisualRenderPath.PersistentWorldPath &&
            !resolution.worldPathUsed &&
            _currentHighConfidenceVisualTimestampNs != _lastWorldRenderFallbackSourceTimestampNs)
        {
            _lastWorldRenderFallbackSourceTimestampNs = _currentHighConfidenceVisualTimestampNs;
            _worldRenderFallbackCount++;
        }

        _latestVisualRenderPathDiagnostics = new VisualRenderPathDiagnostics(
            visualRenderPath,
            _visualRenderPathSegmentId,
            _currentHighConfidenceVisualTimestampNs,
            resolution.worldPathEligible,
            resolution.worldPathUsed,
            resolution.fallbackReason,
            _worldRenderFallbackCount);
        return resolution.rawCameraFromBoard;
    }

    private void ResetVisualTemporalCompensationPredictor(string reason)
    {
        _visualTemporalCompensationPredictor.Clear();
        _latestVisualTemporalCompensationSnapshot = VisualTemporalCompensationSnapshot.Uncompensated(
            visualTemporalCompensationMode,
            _visualTemporalCompensationSegmentId,
            Pose.identity,
            reason);
    }

    /// <summary>
    /// Resolves the single pose that may be rendered by the shared DJI cube.
    /// ShadowDiagnostic intentionally returns None after visual pose staleness.
    /// </summary>
    public bool TryGetRenderCameraFromBoard(
        float visualPoseFreshnessSeconds,
        out Pose cameraFromBoard,
        out DjiRenderPoseSource source,
        out float ageSeconds)
    {
        UpdateShadowPropagationState(visualPoseFreshnessSeconds);

        var visualAgeSeconds = HighConfidenceCameraFromBoardAgeSeconds;
        var propagationAgeSeconds = PropagationAgeSeconds;
        // A valid prediction is eligible to take render authority exactly once
        // at the beginning of a board-loss interval. The dedicated timer starts
        // only after this policy transition has been selected.
        var propagationAgeForPolicySeconds = _propagationStartTimestampNs > RotationPropagationTiming.NoTimestamp
            ? propagationAgeSeconds
            : _shadowPropagationActive && _hasLatestShadowPrediction
                ? 0f
                : float.PositiveInfinity;
        var visualPoseIsRenderEligible =
            CurrentVisualPoseQuality == VisualPoseQuality.HighConfidence &&
            HasHighConfidenceCameraFromBoard &&
            !_hasVisualReacquisitionCandidate;
        source = RotationPropagationRenderPolicy.Resolve(
            visualPoseIsRenderEligible,
            visualAgeSeconds,
            visualPoseFreshnessSeconds,
            IsRotationPropagationRenderingEnabled,
            _hasLatestShadowPrediction,
            propagationAgeForPolicySeconds,
            maxRotationOnlyPropagationSeconds,
            HasLongLivedTelemetryLocalization());

        var previousSource = CurrentRenderPoseSource;
        if (source == DjiRenderPoseSource.Visual && previousSource != DjiRenderPoseSource.Visual)
            CapturePreVisualHandoverSnapshot(previousSource, _currentHighConfidenceVisualTimestampNs);
        if (source == DjiRenderPoseSource.Visual && _worldLocalizationShadow != null)
        {
            // No candidate/pending callback may close the world outage. Commit
            // only after all existing render eligibility gates have passed.
            if (!_worldLocalizationShadow.TryCommitVisualRenderAuthority(
                    CurrentHighConfidenceCameraFromBoard, _currentHighConfidenceVisualTimestampNs, out _))
            {
                // A rejected commit does not stop the coordinator. Resolve the
                // unchanged propagation/fallback policy without visual authority.
                source = RotationPropagationRenderPolicy.Resolve(
                    false, visualAgeSeconds, visualPoseFreshnessSeconds,
                    IsRotationPropagationRenderingEnabled, _hasLatestShadowPrediction,
                    propagationAgeForPolicySeconds, maxRotationOnlyPropagationSeconds,
                    HasLongLivedTelemetryLocalization());
            }
        }
        if (previousSource == DjiRenderPoseSource.Visual && source != DjiRenderPoseSource.Visual)
        {
            ResetVisualTemporalCompensationPredictor("VISUAL_RENDER_AUTHORITY_LOST");
            BeginOutageVisualGroundTruthCapture(DjiBoardVisionBridge.GetMonotonicTimestampNs());
        }

        if (source == DjiRenderPoseSource.Visual)
        {
            var rawVisualPose = ResolveVisualRawRenderPose();
            cameraFromBoard = ResolveVisualTemporalCompensatedRenderPose(rawVisualPose);
            ageSeconds = visualAgeSeconds;
            if (previousSource != DjiRenderPoseSource.Visual)
            {
                if (IsTelemetryPropagationSource(previousSource))
                {
                    QueueReacquisitionDiagnosticsAndStopRendering();
                    Debug.Log(
                        "DJI_RENDER_POSE_SOURCE=VISUAL " +
                        "reason=HIGH_CONFIDENCE_BOARD_REACQUIRED");
                }
                else
                {
                    ResetGuardedHorizontalRenderState("VISUAL_ACCEPTED", DjiBoardVisionBridge.GetMonotonicTimestampNs());
                }

                CompleteOutageVisualGroundTruthCapture(_acceptedVisualReacquisitionMode);
            }
        }
        else if (IsTelemetryPropagationSource(source))
        {
            if (!IsTelemetryPropagationSource(previousSource))
            {
                _propagationStartTimestampNs = RotationPropagationTiming.StartIfNeeded(
                    _propagationStartTimestampNs,
                    DjiBoardVisionBridge.GetMonotonicTimestampNs());
                propagationAgeSeconds = PropagationAgeSeconds;
            }

            if (!IsTelemetryPropagationSource(previousSource))
            {
                _worldLocalizationShadow ??= FindFirstObjectByType<DjiWorldLocalizationShadow>();
                _worldLocalizationShadow?.BeginRotationPropagation(
                    _shadowRotationPropagationBaseline.cameraFromBoardVisual,
                    _shadowRotationPropagationBaseline.timestampNs,
                    _shadowRotationPropagationBaseline.navigationFromCameraTelemetry);
            }

            // Step 8G promotes only the persistent-world GNSS-only smoother.
            // The older guarded board-relative policy remains diagnostic and
            // cannot replace this authoritative world-localization pose.
            if (_hasLatestShadowPrediction)
            {
                _worldLocalizationShadow?.UpdateRotationPropagation(
                    DjiBoardVisionBridge.GetMonotonicTimestampNs(),
                    _latestShadowPredictionTimestampNs,
                    _latestShadowPredictionNavigationFromCamera,
                    _latestShadowPredictedCameraFromBoard);
            }

            if (!TryResolvePersistentWorldPropagationRenderPose(out cameraFromBoard))
                cameraFromBoard = _latestShadowPredictedCameraFromBoard;
            ageSeconds = propagationAgeSeconds;
        }
        else
        {
            if (HasRenderedRotationPropagationTimedOut(propagationAgeSeconds))
            {
                LogRotationPropagationExpirationIfNeeded(propagationAgeSeconds, visualAgeSeconds);
                ExpireLocalizationSessionAfterPropagationTimeout();
            }
            else
            {
                ResetGuardedHorizontalRenderState("RENDER_SOURCE_NONE", DjiBoardVisionBridge.GetMonotonicTimestampNs());
            }
            cameraFromBoard = Pose.identity;
            ageSeconds = float.PositiveInfinity;
        }

        if (IsTelemetryPropagationSource(source) && !IsTelemetryPropagationSource(previousSource))
        {
            var baseline = _shadowRotationPropagationBaseline;
            var currentTelemetryRotation = _telemetryProvider != null
                ? _telemetryProvider.CurrentNavigationCameraPose.rotation
                : baseline.navigationFromCameraTelemetry;
            var deltaRotation = ReferenceFrameTransforms.CameraTFromCamera0Rotation(
                baseline.navigationFromCameraTelemetry,
                currentTelemetryRotation);
            Debug.Log(
                "DJI_RENDER_POSE_SOURCE=TELEMETRY_PROPAGATED " +
                $"ageMs={propagationAgeSeconds * 1000f:F0} " +
                $"reason={(CurrentVisualPoseQuality == VisualPoseQuality.Degraded ? "VISUAL_POSE_DEGRADED" : "VISUAL_POSE_UNAVAILABLE")} " +
                $"deltaRotationDeg={Quaternion.Angle(Quaternion.identity, deltaRotation):F2}");
        }
        LogRotationPropagationState(source, visualAgeSeconds, propagationAgeSeconds,
            force: source != previousSource);

        CurrentRenderPoseSource = source;
        if (source != previousSource)
        {
            _telemetryValidationLogger?.RecordMotionRenderSourceTransition(
                DjiBoardVisionBridge.GetMonotonicTimestampNs(), previousSource, source);
            TraceOrientationAuthorityTransition(previousSource, source);
        }
        if (source == DjiRenderPoseSource.Visual)
            RecordAuthoritativeVisualPose();
        return source != DjiRenderPoseSource.None;
    }

    private DjiCameraCalibration _calibration;
    private DjiCameraPoseProvider _telemetryProvider;
    private DjiTelemetryValidationLogger _telemetryValidationLogger;
    private int _lastFrameSequence = -1;
    private float _nextDiagnosticLogTime;
    private float _lastAcceptedCameraFromBoardRealtime = float.NegativeInfinity;
    private float _lastHighConfidenceCameraFromBoardRealtime = float.NegativeInfinity;
    private Pose _lastVisualQualityPose;
    private bool _hasLastVisualQualityPose;
    private long _lastVisualQualityPoseTimestampNs;
    private Quaternion _lastVisualQualityTelemetryRotation;
    private bool _hasLastVisualQualityTelemetryRotation;
    private VisualReacquisitionMode _visualReacquisitionMode;
    private int _consecutiveHighConfidenceVisualPoses;
    private int _consecutiveBorderlineFourMarkerQualityFailures;
    private int _consecutiveTranslationJumpSuspects;
    private bool _holdingVisualAuthorityForBorderlineQuality;
    private bool _holdingVisualAuthorityForTranslationJumpSuspect;
    private VisualTranslationJumpEvaluation _latestTranslationJumpEvaluation;
    private VisualTranslationJumpDecision _lastLoggedTranslationJumpDecision = VisualTranslationJumpDecision.Accept;
    private const int VisualPoseTransitionHistoryCapacity = 20;
    private readonly Queue<VisualPoseMetrics> _visualPoseTransitionHistory = new();

    private readonly struct VisualPoseQualityEvaluation
    {
        public readonly string reason;
        public readonly FourMarkerVisualQualityFailureSeverity severity;

        public VisualPoseQualityEvaluation(
            string reason,
            FourMarkerVisualQualityFailureSeverity severity)
        {
            this.reason = reason;
            this.severity = severity;
        }

        public static VisualPoseQualityEvaluation Pass => new(
            string.Empty,
            FourMarkerVisualQualityFailureSeverity.Pass);
    }
    private readonly List<float> _stationaryPositionDeltas = new();
    private readonly List<float> _stationaryRotationDeltas = new();
    private readonly List<float> _provisionalReprojectionRms = new();
    private readonly List<float> _provisionalMaxResiduals = new();
    private int _provisionalRawAcceptedSampleCount;
    private readonly ValidationBucketStatistics[] _validationBucketStatistics =
    {
        new("CENTER"), new("LEFT"), new("RIGHT"), new("TOP"), new("BOTTOM"),
        new("X_TILT"), new("Y_TILT"), new("NEAR"), new("FAR")
    };
    private Texture2D _debugLineTexture;
    private const int MinimumOffsetScanSamplesForClassification = 20;
    private const int MinimumControlledTestSamples = 50;
    private static readonly int[] RotationOffsetScanMilliseconds = { -200, -150, -100, -50, 0, 50, 100 };
    private static readonly int[] RotationErrorHorizonsMilliseconds = { 250, 500, 1000, 2000, 5000 };
    private readonly Dictionary<int, RotationErrorStatistics> _rotationOffsetStatistics = new();
    private readonly Dictionary<int, RotationErrorStatistics> _rotationErrorByHorizonStatistics = new();
    private readonly Dictionary<int, Quaternion> _baselineTelemetryRotationsByOffset = new();
    private readonly List<PendingRotationDiagnosticSample> _pendingRotationDiagnosticSamples = new();
    private readonly List<PairedRotationDiagnosticSample> _pairedRotationDiagnosticSamples = new();
    private readonly Dictionary<ControlledRotationTest, ControlledRotationTestStatistics> _controlledRotationTestStatistics = new();
    private RotationComparisonBaseline _rotationComparisonBaseline;
    private bool _hasRotationComparisonBaseline;
    private int _rotationComparisonBaselineId;
    private bool _rotationConventionSanityLogged;
    private DjiCameraPoseProvider _telemetryProviderWithShadowSubscription;
    private PairedRotationDiagnosticSample _lastPairedRotationDiagnosticSample;
    private PairedRotationDiagnosticSample _stationaryRotationDiagnosticReference;
    private bool _hasLastPairedRotationDiagnosticSample;
    // Validated on the physical Mini 3 Pro with controlled signed-yaw and gimbal-pitch sweeps.
    private const bool SafeForRotationOnlyPropagation = true;
    private ShadowRotationPropagationBaseline _shadowRotationPropagationBaseline;
    private bool _hasShadowRotationPropagationBaseline;
    private bool _shadowPropagationActive;
    private bool _hasLatestShadowPrediction;
    private Pose _latestShadowPredictedCameraFromBoard;
    // This timer starts only when rendering actually changes from VISUAL to
    // TELEMETRY_PROPAGATED. It is deliberately independent of the pose baseline.
    private long _propagationStartTimestampNs;
    private bool _hasPendingReacquisition;
    private ShadowRotationPropagationBaseline _pendingReacquisitionBaseline;
    private long _pendingReacquisitionStartTimestampNs;
    private bool _hasVisualReacquisitionCandidate;
    private DjiOutageReacquisitionMode _acceptedVisualReacquisitionMode = DjiOutageReacquisitionMode.PropagatedReacquisition;
    private NativeBoardResult _visualReacquisitionCandidateResult;
    private VisualPoseMetrics _visualReacquisitionCandidateMetrics;
    private long _visualReacquisitionStartTimestampNs;
    private bool _visualReacquisitionCandidateShouldLogDiagnosticSnapshot;
    private bool _reacquisitionPendingLogged;
    private float _nextShadowPropagationLogTime;
    private float _nextTranslationShadowLogTime;
    private float _nextFreshTranslationShadowLogTime;
    private float _nextHorizontalGnssTranslationShadowLogTime;
    private float _nextHybridPositionShadowLogTime;
    private float _nextTranslationShadowInvalidLogTime;
    private float _nextVelocityScaleDiagnosticLogTime;
    private float _nextRotationPropagationStateLogTime;
    private bool _rotationPropagationExpirationLogged;
    private bool _hasLatestTranslationShadowPrediction;
    private Pose _latestTranslationShadowCameraFromBoard;
    private Vector3 _latestTranslationShadowNavigationDisplacement;
    private long _latestTranslationShadowTimestampNs;
    private long _translationShadowStartTimestampNs;
    private readonly Dictionary<ReacquisitionDurationBucket, RotationErrorStatistics> _shadowReacquisitionStatistics = new();
    private readonly Dictionary<ReacquisitionDurationBucket, HorizontalGnssShadowStatistics> _horizontalGnssShadowStatistics = new();
    private readonly Dictionary<ReacquisitionDurationBucket, HybridPositionShadowStatistics> _hybridPositionShadowStatistics = new();
    private bool _hasLatestHybridPositionShadow;
    private HybridPositionShadowSnapshot _latestHybridPositionShadow;
    private readonly GuardedHorizontalRenderStateMachine _guardedHorizontalRenderStateMachine = new();
    private GuardedHorizontalRenderSnapshot _latestGuardedHorizontalRenderSnapshot;
    private bool _hasLatestGuardedHorizontalRenderSnapshot;
    private GuardedHorizontalTranslationState _lastLoggedGuardedHorizontalState = GuardedHorizontalTranslationState.Off;
    private bool _hasLatestGuardedHorizontalRenderedPose;
    private Pose _latestGuardedHorizontalRenderedPose;
    // Debug-only A/B grouping. It is session-local and starts at zero whenever
    // DroneView creates a new provider.
    private int _horizontalModeSegmentId;
    // TEMP A/B segments are independent from the rendered-horizontal GNSS A/B
    // selection and never enter outage event ground truth.
    private int _visualTemporalCompensationSegmentId;
    // WORLD A/B segments are independent from TEMP and HORIZ experiments.
    private int _visualRenderPathSegmentId;
    private int _worldRenderFallbackCount;
    private long _lastWorldRenderFallbackSourceTimestampNs;
    private DjiWorldLocalizationShadow _worldLocalizationShadow;
    private VisualRenderPathDiagnostics _latestVisualRenderPathDiagnostics;
    private readonly VisualTemporalCompensationPredictor _visualTemporalCompensationPredictor = new();
    private VisualTemporalCompensationSnapshot _latestVisualTemporalCompensationSnapshot;
    // Event-level measurement state only. It never participates in resolving
    // VISUAL / TELEMETRY_PROPAGATED / NONE render authority.
    private readonly DjiOutageVisualGroundTruthTracker _outageVisualGroundTruth = new();
    private int _localizationSessionId;
    private DjiOutageVisualGroundTruthTracker.TrustedVisualPose _lastAuthoritativeVisualPose;
    private long _currentHighConfidenceVisualTimestampNs;
    // Kept only for temporal diagnostics when this exact visual pose is
    // applied by the shared overlay in LateUpdate.
    private VisualPoseTimingSample _authoritativeVisualTimingSample;
    private bool _hasAuthoritativeVisualTimingSample;
    private bool _hasPendingOutagePredictionCapture;
    private OutagePredictionCapture _pendingOutagePredictionCapture;
    private long _latestShadowPredictionTimestampNs;
    private Quaternion _latestShadowPredictionNavigationFromCamera = Quaternion.identity;
    private static readonly int[] VelocityVisualValidationWindowMilliseconds = { 250, 500, 1000 };
    private const int VelocityVisualValidationHistoryCapacity = 64;
    private readonly List<VelocityVisualValidationPose> _velocityVisualValidationPoses = new();
    private readonly VelocityVisualValidationStatistics _velocityVisualValidationStatistics = new();
    private readonly Dictionary<int, float> _nextVelocityVisualValidationLogTimes = new();
    private float _nextVelocityVisualValidationSummaryLogTime;
    private static readonly int[] PositionVisualValidationWindowMilliseconds = { 500, 1000, 2000 };
    private const int PositionVisualValidationHistoryCapacity = 64;
    private readonly List<PositionVisualValidationPose> _positionVisualValidationPoses = new();
    private readonly Dictionary<int, float> _nextPositionVisualValidationLogTimes = new();
    private bool _hasPositionVisualBaseline;
    private readonly YawLegValidationTracker _yawLegValidation = new();

    private readonly struct OutagePredictionCapture
    {
        public readonly long postVisualTimestampNs;
        public readonly double propagationDurationMilliseconds;
        public readonly DjiOutagePredictionPose rotationOnly;
        public readonly DjiOutagePredictionPose guardedRendered;
        public readonly DjiOutagePredictionPose gnssShadow;
        public readonly DjiOutagePredictionPose hybridShadow;
        public readonly GuardedHorizontalRenderSnapshot guardedSnapshot;

        public OutagePredictionCapture(
            long postVisualTimestampNs,
            double propagationDurationMilliseconds,
            DjiOutagePredictionPose rotationOnly,
            DjiOutagePredictionPose guardedRendered,
            DjiOutagePredictionPose gnssShadow,
            DjiOutagePredictionPose hybridShadow,
            GuardedHorizontalRenderSnapshot guardedSnapshot)
        {
            this.postVisualTimestampNs = postVisualTimestampNs;
            this.propagationDurationMilliseconds = propagationDurationMilliseconds;
            this.rotationOnly = rotationOnly;
            this.guardedRendered = guardedRendered;
            this.gnssShadow = gnssShadow;
            this.hybridShadow = hybridShadow;
            this.guardedSnapshot = guardedSnapshot;
        }
    }

    private bool IsRotationPropagationRenderingEnabled => RotationPropagationRenderPolicy.IsEnabledAllowed(
        rotationPropagationMode,
        SafeForRotationOnlyPropagation,
        allowUnvalidatedRotationPropagationForDeveloperTesting);

    private float BaselineAgeSeconds => _hasShadowRotationPropagationBaseline
        ? Mathf.Max(0f, (float)((DjiBoardVisionBridge.GetMonotonicTimestampNs() - _shadowRotationPropagationBaseline.timestampNs) / 1_000_000_000.0))
        : float.PositiveInfinity;

    private float PropagationAgeSeconds => RotationPropagationTiming.AgeSeconds(
        DjiBoardVisionBridge.GetMonotonicTimestampNs(),
        _propagationStartTimestampNs);

    private enum ReacquisitionDurationBucket
    {
        Under500Milliseconds,
        From500MillisecondsTo1Second,
        From1To2Seconds,
        From2To5Seconds,
        Over5Seconds
    }

    private readonly struct ShadowRotationPropagationBaseline
    {
        public readonly long timestampNs;
        public readonly Pose cameraFromBoardVisual;
        public readonly Quaternion navigationFromCameraTelemetry;
        public readonly bool hasHorizontalGnssPosition;
        public readonly TelemetryAircraftPositionSample horizontalGnssPosition;

        public ShadowRotationPropagationBaseline(
            long timestampNs,
            Pose cameraFromBoardVisual,
            Quaternion navigationFromCameraTelemetry,
            bool hasHorizontalGnssPosition = false,
            TelemetryAircraftPositionSample horizontalGnssPosition = default)
        {
            this.timestampNs = timestampNs;
            this.cameraFromBoardVisual = cameraFromBoardVisual;
            this.navigationFromCameraTelemetry = navigationFromCameraTelemetry;
            this.hasHorizontalGnssPosition = hasHorizontalGnssPosition;
            this.horizontalGnssPosition = horizontalGnssPosition;
        }
    }

    private readonly struct HybridPositionShadowPrediction
    {
        public readonly Pose cameraFromBoard;
        public readonly HybridPositionShadowSnapshot snapshot;
        public readonly bool hasTranslationCorrection;
        public readonly ReferenceFrameTransforms.VelocityStateHistoryCoverage velocityCoverage;

        public HybridPositionShadowPrediction(
            Pose cameraFromBoard,
            HybridPositionShadowSnapshot snapshot,
            bool hasTranslationCorrection,
            ReferenceFrameTransforms.VelocityStateHistoryCoverage velocityCoverage)
        {
            this.cameraFromBoard = cameraFromBoard;
            this.snapshot = snapshot;
            this.hasTranslationCorrection = hasTranslationCorrection;
            this.velocityCoverage = velocityCoverage;
        }
    }

    private readonly struct VisualPoseMetrics
    {
        public readonly long timestampNs;
        public readonly int markerCount;
        public readonly int correspondenceCount;
        public readonly string markerIds;
        public readonly float reprojectionRmsPixels;
        public readonly float maxCornerResidualPixels;
        public readonly float observedMarkerSizePixels;
        public readonly float normalizedReprojectionRms;
        public readonly float normalizedMaxCornerResidual;
        public readonly float minimumBorderDistancePixels;
        public readonly float detectedBoundsAreaPixels;
        public readonly Pose cameraFromBoard;
        public readonly float translationJumpMeters;
        public readonly float rotationJumpDegrees;
        public readonly float visualPoseDeltaSeconds;
        public readonly bool hasTelemetryRotationPrediction;
        public readonly float rotationCompensatedTranslationResidualMeters;
        public readonly bool hasTelemetrySpeed;
        public readonly float telemetrySpeedMetersPerSecond;

        public VisualPoseMetrics(
            long timestampNs,
            int markerCount,
            int correspondenceCount,
            string markerIds,
            float reprojectionRmsPixels,
            float maxCornerResidualPixels,
            float observedMarkerSizePixels,
            float normalizedReprojectionRms,
            float normalizedMaxCornerResidual,
            float minimumBorderDistancePixels,
            float detectedBoundsAreaPixels,
            Pose cameraFromBoard,
            float translationJumpMeters,
            float rotationJumpDegrees,
            float visualPoseDeltaSeconds,
            bool hasTelemetryRotationPrediction,
            float rotationCompensatedTranslationResidualMeters,
            bool hasTelemetrySpeed,
            float telemetrySpeedMetersPerSecond)
        {
            this.timestampNs = timestampNs;
            this.markerCount = markerCount;
            this.correspondenceCount = correspondenceCount;
            this.markerIds = markerIds;
            this.reprojectionRmsPixels = reprojectionRmsPixels;
            this.maxCornerResidualPixels = maxCornerResidualPixels;
            this.observedMarkerSizePixels = observedMarkerSizePixels;
            this.normalizedReprojectionRms = normalizedReprojectionRms;
            this.normalizedMaxCornerResidual = normalizedMaxCornerResidual;
            this.minimumBorderDistancePixels = minimumBorderDistancePixels;
            this.detectedBoundsAreaPixels = detectedBoundsAreaPixels;
            this.cameraFromBoard = cameraFromBoard;
            this.translationJumpMeters = translationJumpMeters;
            this.rotationJumpDegrees = rotationJumpDegrees;
            this.visualPoseDeltaSeconds = visualPoseDeltaSeconds;
            this.hasTelemetryRotationPrediction = hasTelemetryRotationPrediction;
            this.rotationCompensatedTranslationResidualMeters = rotationCompensatedTranslationResidualMeters;
            this.hasTelemetrySpeed = hasTelemetrySpeed;
            this.telemetrySpeedMetersPerSecond = telemetrySpeedMetersPerSecond;
        }

        public static VisualPoseMetrics Unavailable(long timestampNs) => new(
            timestampNs,
            markerCount: 0,
            correspondenceCount: 0,
            markerIds: string.Empty,
            reprojectionRmsPixels: float.PositiveInfinity,
            maxCornerResidualPixels: float.PositiveInfinity,
            observedMarkerSizePixels: float.NaN,
            normalizedReprojectionRms: float.PositiveInfinity,
            normalizedMaxCornerResidual: float.PositiveInfinity,
            minimumBorderDistancePixels: 0f,
            detectedBoundsAreaPixels: 0f,
            cameraFromBoard: Pose.identity,
            translationJumpMeters: 0f,
            rotationJumpDegrees: 0f,
            visualPoseDeltaSeconds: 0f,
            hasTelemetryRotationPrediction: false,
            rotationCompensatedTranslationResidualMeters: float.NaN,
            hasTelemetrySpeed: false,
            telemetrySpeedMetersPerSecond: float.NaN);
    }

    private readonly struct RotationComparisonBaseline
    {
        public readonly long visionTimestampNs;
        public readonly Quaternion cameraFromBoardRotation;
        public readonly Quaternion navigationFromCameraRotation;

        public RotationComparisonBaseline(
            long visionTimestampNs,
            Quaternion cameraFromBoardRotation,
            Quaternion navigationFromCameraRotation)
        {
            this.visionTimestampNs = visionTimestampNs;
            this.cameraFromBoardRotation = cameraFromBoardRotation;
            this.navigationFromCameraRotation = navigationFromCameraRotation;
        }
    }

    private readonly struct InterpolatedTelemetryRotation
    {
        public readonly Quaternion navigationFromCameraRotation;
        public readonly long beforeTimestampNs;
        public readonly long afterTimestampNs;
        public readonly float interpolationAlpha;
        public readonly float nearestDeltaMilliseconds;
        public readonly float aircraftYawDegrees;
        public readonly float gimbalPitchDegrees;

        public InterpolatedTelemetryRotation(
            Quaternion navigationFromCameraRotation,
            long beforeTimestampNs,
            long afterTimestampNs,
            float interpolationAlpha,
            float nearestDeltaMilliseconds,
            float aircraftYawDegrees,
            float gimbalPitchDegrees)
        {
            this.navigationFromCameraRotation = navigationFromCameraRotation;
            this.beforeTimestampNs = beforeTimestampNs;
            this.afterTimestampNs = afterTimestampNs;
            this.interpolationAlpha = interpolationAlpha;
            this.nearestDeltaMilliseconds = nearestDeltaMilliseconds;
            this.aircraftYawDegrees = aircraftYawDegrees;
            this.gimbalPitchDegrees = gimbalPitchDegrees;
        }
    }

    private enum ControlledRotationTest
    {
        Yaw,
        GimbalPitch
    }

    private readonly struct PairedRotationDiagnosticSample
    {
        public readonly long timestampNs;
        public readonly Quaternion cameraFromBoardRotation;
        public readonly Quaternion navigationFromCameraRotation;
        public readonly float aircraftYawDegrees;
        public readonly float gimbalPitchDegrees;

        public PairedRotationDiagnosticSample(
            long timestampNs,
            Quaternion cameraFromBoardRotation,
            Quaternion navigationFromCameraRotation,
            float aircraftYawDegrees,
            float gimbalPitchDegrees)
        {
            this.timestampNs = timestampNs;
            this.cameraFromBoardRotation = cameraFromBoardRotation;
            this.navigationFromCameraRotation = navigationFromCameraRotation;
            this.aircraftYawDegrees = aircraftYawDegrees;
            this.gimbalPitchDegrees = gimbalPitchDegrees;
        }
    }

    // This deliberately retains only an accepted visual pose and its timestamp;
    // no native image, video buffer, or Unity render state crosses this boundary.
    private readonly struct PendingRotationDiagnosticSample
    {
        public readonly long visionTimestampNs;
        public readonly Pose cameraFromBoard;
        public readonly int markerCount;
        public readonly long enqueuedTimestampNs;
        public readonly int baselineId;
        public readonly bool shouldLogDiagnosticSnapshot;

        public PendingRotationDiagnosticSample(
            long visionTimestampNs,
            Pose cameraFromBoard,
            int markerCount,
            long enqueuedTimestampNs,
            int baselineId,
            bool shouldLogDiagnosticSnapshot)
        {
            this.visionTimestampNs = visionTimestampNs;
            this.cameraFromBoard = cameraFromBoard;
            this.markerCount = markerCount;
            this.enqueuedTimestampNs = enqueuedTimestampNs;
            this.baselineId = baselineId;
            this.shouldLogDiagnosticSnapshot = shouldLogDiagnosticSnapshot;
        }
    }

    private enum VelocityVisualMotionClass
    {
        TranslationDominant,
        RotationDominant,
        Mixed
    }

    // This is an owned, timestamped pose sample. It deliberately contains no
    // video data and has no relationship to the render-pose resolver.
    private readonly struct VelocityVisualValidationPose
    {
        public readonly long timestampNs;
        public readonly Pose cameraFromBoard;
        public readonly Quaternion navigationFromCamera;
        public readonly float aircraftYawDegrees;
        public readonly float gimbalPitchDegrees;

        public VelocityVisualValidationPose(
            long timestampNs,
            Pose cameraFromBoard,
            Quaternion navigationFromCamera,
            float aircraftYawDegrees,
            float gimbalPitchDegrees)
        {
            this.timestampNs = timestampNs;
            this.cameraFromBoard = cameraFromBoard;
            this.navigationFromCamera = navigationFromCamera;
            this.aircraftYawDegrees = aircraftYawDegrees;
            this.gimbalPitchDegrees = gimbalPitchDegrees;
        }
    }

    private readonly struct PositionVisualValidationPose
    {
        public readonly long timestampNs;
        public readonly Pose cameraFromBoard;
        public readonly Quaternion navigationFromCamera;

        public PositionVisualValidationPose(
            long timestampNs,
            Pose cameraFromBoard,
            Quaternion navigationFromCamera)
        {
            this.timestampNs = timestampNs;
            this.cameraFromBoard = cameraFromBoard;
            this.navigationFromCamera = navigationFromCamera;
        }
    }

    private sealed class VelocityVisualValidationStatistics
    {
        private readonly List<float> _scaleRatios = new();
        private readonly List<float> _directionErrorsDegrees = new();
        private int _northComparableCount;
        private int _northSignAgreementCount;
        private int _eastComparableCount;
        private int _eastSignAgreementCount;
        private int _downComparableCount;
        private int _downSignAgreementCount;

        public int TranslationDominantMeaningfulCount { get; private set; }
        public int ZeroVelocityDisagreementCount { get; private set; }

        public void Add(
            VelocityVisualMotionClass motionClass,
            bool meaningfulVisualMotion,
            bool velocityCoverageValid,
            Vector3 visualDisplacement,
            Vector3 velocityDisplacement,
            float nearZeroVelocityDistance,
            float minimumAxisDistance)
        {
            if (!meaningfulVisualMotion || motionClass != VelocityVisualMotionClass.TranslationDominant ||
                !velocityCoverageValid)
            {
                return;
            }

            TranslationDominantMeaningfulCount++;
            var visualDistance = visualDisplacement.magnitude;
            var velocityDistance = velocityDisplacement.magnitude;
            if (velocityDistance <= nearZeroVelocityDistance)
            {
                ZeroVelocityDisagreementCount++;
                return;
            }

            _scaleRatios.Add(velocityDistance / visualDistance);
            _directionErrorsDegrees.Add(Vector3.Angle(visualDisplacement, velocityDisplacement));
            AddAxis(visualDisplacement.x, velocityDisplacement.x, minimumAxisDistance,
                ref _northComparableCount, ref _northSignAgreementCount);
            AddAxis(visualDisplacement.y, velocityDisplacement.y, minimumAxisDistance,
                ref _eastComparableCount, ref _eastSignAgreementCount);
            AddAxis(visualDisplacement.z, velocityDisplacement.z, minimumAxisDistance,
                ref _downComparableCount, ref _downSignAgreementCount);
        }

        public string Summary()
        {
            return $"translationDominantMeaningfulSamples={TranslationDominantMeaningfulCount} " +
                   $"scaleMedian={Format(DjiBoardVisionProvider.Percentile(_scaleRatios, 0.5f))} " +
                   $"scaleP25={Format(DjiBoardVisionProvider.Percentile(_scaleRatios, 0.25f))} " +
                   $"scaleP75={Format(DjiBoardVisionProvider.Percentile(_scaleRatios, 0.75f))} " +
                   $"scaleP95={Format(DjiBoardVisionProvider.Percentile(_scaleRatios, 0.95f))} " +
                   $"directionMedianDeg={Format(DjiBoardVisionProvider.Percentile(_directionErrorsDegrees, 0.5f))} " +
                   $"directionP95Deg={Format(DjiBoardVisionProvider.Percentile(_directionErrorsDegrees, 0.95f))} " +
                   $"northSignAgreementPct={FormatPercent(_northSignAgreementCount, _northComparableCount)} " +
                   $"eastSignAgreementPct={FormatPercent(_eastSignAgreementCount, _eastComparableCount)} " +
                   $"downSignAgreementPct={FormatPercent(_downSignAgreementCount, _downComparableCount)} " +
                   $"zeroVelocityDisagreementCount={ZeroVelocityDisagreementCount}";
        }

        private static void AddAxis(
            float visualAxis,
            float velocityAxis,
            float minimumAxisDistance,
            ref int comparableCount,
            ref int agreementCount)
        {
            if (Mathf.Abs(visualAxis) < minimumAxisDistance || Mathf.Abs(velocityAxis) < minimumAxisDistance)
                return;

            comparableCount++;
            if (Mathf.Sign(visualAxis) == Mathf.Sign(velocityAxis))
                agreementCount++;
        }

        private static string Format(float value) => float.IsFinite(value) ? value.ToString("F3") : "UNAVAILABLE";

        private static string FormatPercent(int numerator, int denominator) => denominator > 0
            ? (numerator / (float)denominator * 100f).ToString("F1")
            : "UNAVAILABLE";
    }

    private sealed class RotationErrorStatistics
    {
        private readonly List<float> _errors = new();

        public int Count => _errors.Count;
        public float MeanDegrees => Mean(_errors);
        public float MedianDegrees => Percentile(_errors, 0.5f);
        public float P95Degrees => Percentile(_errors, 0.95f);
        public float MaxDegrees => Max(_errors);

        public void Add(float errorDegrees) => _errors.Add(errorDegrees);
        public void Clear() => _errors.Clear();
    }

    /// <summary>
    /// Per-outage horizontal error summaries for diagnostics. These values are
    /// intentionally never read by the render-pose resolver.
    /// </summary>
    private sealed class HorizontalGnssShadowStatistics
    {
        private readonly List<float> _rotationOnlyErrors = new();
        private readonly List<float> _freshVelocityErrors = new();
        private readonly List<float> _gnssErrors = new();

        public int TotalComparisons { get; private set; }
        public int ValidGnssComparisons { get; private set; }
        public int InvalidGnssComparisons { get; private set; }
        public int GnssBetterThanRotationOnlyCount { get; private set; }
        public int GnssWorseThanRotationOnlyCount { get; private set; }
        public int FreshVelocityBetterThanGnssCount { get; private set; }
        public int GnssBetterThanFreshVelocityCount { get; private set; }

        public void Add(
            float rotationOnlyHorizontalError,
            bool hasFreshVelocity,
            float freshVelocityHorizontalError,
            bool hasGnss,
            float gnssHorizontalError)
        {
            TotalComparisons++;
            _rotationOnlyErrors.Add(rotationOnlyHorizontalError);
            if (hasFreshVelocity)
                _freshVelocityErrors.Add(freshVelocityHorizontalError);

            if (!hasGnss)
            {
                InvalidGnssComparisons++;
                return;
            }

            ValidGnssComparisons++;
            _gnssErrors.Add(gnssHorizontalError);
            if (gnssHorizontalError < rotationOnlyHorizontalError)
                GnssBetterThanRotationOnlyCount++;
            else if (gnssHorizontalError > rotationOnlyHorizontalError)
                GnssWorseThanRotationOnlyCount++;

            if (!hasFreshVelocity)
                return;

            if (freshVelocityHorizontalError < gnssHorizontalError)
                FreshVelocityBetterThanGnssCount++;
            else if (gnssHorizontalError < freshVelocityHorizontalError)
                GnssBetterThanFreshVelocityCount++;
        }

        public string Summary() =>
            $"totalComparisons={TotalComparisons} validGnssComparisons={ValidGnssComparisons} invalidGnssComparisons={InvalidGnssComparisons} " +
            $"rotationOnlyHorizontalMedianM={Format(DjiBoardVisionProvider.Percentile(_rotationOnlyErrors, 0.5f))} rotationOnlyHorizontalP95M={Format(DjiBoardVisionProvider.Percentile(_rotationOnlyErrors, 0.95f))} " +
            $"freshVelocityHorizontalMedianM={Format(DjiBoardVisionProvider.Percentile(_freshVelocityErrors, 0.5f))} freshVelocityHorizontalP95M={Format(DjiBoardVisionProvider.Percentile(_freshVelocityErrors, 0.95f))} " +
            $"gnssHorizontalMedianM={Format(DjiBoardVisionProvider.Percentile(_gnssErrors, 0.5f))} gnssHorizontalP95M={Format(DjiBoardVisionProvider.Percentile(_gnssErrors, 0.95f))} " +
            $"gnssBetterThanRotationOnly={GnssBetterThanRotationOnlyCount} gnssWorseThanRotationOnly={GnssWorseThanRotationOnlyCount} " +
            $"freshVelocityBetterThanGnss={FreshVelocityBetterThanGnssCount} gnssBetterThanFreshVelocity={GnssBetterThanFreshVelocityCount}";

        private static string Format(float value) => float.IsFinite(value) ? value.ToString("F4") : "UNAVAILABLE";
    }

    private sealed class HybridPositionShadowStatistics
    {
        private readonly List<float> _rotationOnlyErrors = new();
        private readonly List<float> _freshVelocityErrors = new();
        private readonly List<float> _gnssErrors = new();
        private readonly List<float> _hybridErrors = new();

        public int EventCount { get; private set; }
        public int ValidHybridCount { get; private set; }
        public int HybridBetterThanRotationOnlyCount { get; private set; }
        public int HybridBetterThanGnssCount { get; private set; }
        public int HybridBetterThanFreshVelocityCount { get; private set; }

        public void Add(
            float rotationOnlyError,
            float freshVelocityError,
            float gnssError,
            float hybridError,
            bool hybridHasTranslation)
        {
            EventCount++;
            _rotationOnlyErrors.Add(rotationOnlyError);
            if (float.IsFinite(freshVelocityError))
                _freshVelocityErrors.Add(freshVelocityError);
            if (float.IsFinite(gnssError))
                _gnssErrors.Add(gnssError);
            if (!hybridHasTranslation || !float.IsFinite(hybridError))
                return;

            ValidHybridCount++;
            _hybridErrors.Add(hybridError);
            if (hybridError < rotationOnlyError)
                HybridBetterThanRotationOnlyCount++;
            if (float.IsFinite(gnssError) && hybridError < gnssError)
                HybridBetterThanGnssCount++;
            if (float.IsFinite(freshVelocityError) && hybridError < freshVelocityError)
                HybridBetterThanFreshVelocityCount++;
        }

        public string Summary() =>
            $"eventCount={EventCount} validHybridCount={ValidHybridCount} " +
            $"rotationOnlyHorizontalMedianM={Format(DjiBoardVisionProvider.Percentile(_rotationOnlyErrors, 0.5f))} " +
            $"freshVelocityHorizontalMedianM={Format(DjiBoardVisionProvider.Percentile(_freshVelocityErrors, 0.5f))} " +
            $"gnssHorizontalMedianM={Format(DjiBoardVisionProvider.Percentile(_gnssErrors, 0.5f))} " +
            $"hybridHorizontalMedianM={Format(DjiBoardVisionProvider.Percentile(_hybridErrors, 0.5f))} " +
            $"hybridBetterThanRotationOnly={HybridBetterThanRotationOnlyCount} " +
            $"hybridBetterThanGnss={HybridBetterThanGnssCount} " +
            $"hybridBetterThanFreshVelocity={HybridBetterThanFreshVelocityCount}";

        private static string Format(float value) => float.IsFinite(value) ? value.ToString("F4") : "UNAVAILABLE";
    }

    private sealed class ControlledRotationTestStatistics
    {
        public readonly ControlledRotationTest Test;
        public readonly RotationErrorStatistics Errors = new();
        public readonly Dictionary<int, RotationErrorStatistics> OffsetErrors = new();
        public readonly Dictionary<int, Quaternion> BaselineTelemetryRotationsByOffset = new();
        public bool HasBaseline;
        public PairedRotationDiagnosticSample Baseline;
        public float VisualRotationRangeDegrees;
        public float TelemetryRotationRangeDegrees;
        public float LastVisualSignedDeltaDegrees;
        public float LastTelemetrySignedDeltaDegrees;

        public ControlledRotationTestStatistics(ControlledRotationTest test) => Test = test;

        public void Reset()
        {
            HasBaseline = false;
            Errors.Clear();
            VisualRotationRangeDegrees = 0f;
            TelemetryRotationRangeDegrees = 0f;
            LastVisualSignedDeltaDegrees = 0f;
            LastTelemetrySignedDeltaDegrees = 0f;
            OffsetErrors.Clear();
            BaselineTelemetryRotationsByOffset.Clear();
        }
    }

    private enum YawValidationLeg
    {
        None,
        CenterToLeft,
        LeftToCenter,
        CenterToRight,
        RightToCenter
    }

    private sealed class YawLegStatistics
    {
        public readonly YawValidationLeg Leg;
        public readonly PairedRotationDiagnosticSample Baseline;
        public readonly RotationErrorStatistics QuaternionErrors = new();
        public int PairedSamples;
        public int SignedDirectionMismatches;
        public float MaximumGimbalPitchChangeDegrees;
        public float LastVisualSignedYawDegrees;
        public float LastTelemetrySignedYawDegrees;

        public YawLegStatistics(YawValidationLeg leg, PairedRotationDiagnosticSample baseline)
        {
            Leg = leg;
            Baseline = baseline;
        }
    }

    private sealed class YawLegValidationTracker
    {
        public bool HasCenter;
        public PairedRotationDiagnosticSample Center;
        public YawLegStatistics ActiveLeg;
        public PairedRotationDiagnosticSample Peak;
        public float PeakTelemetrySignedYawDegrees;
        public float LastTelemetrySignedYawDegrees;

        public void Reset()
        {
            HasCenter = false;
            ActiveLeg = null;
            PeakTelemetrySignedYawDegrees = 0f;
            LastTelemetrySignedYawDegrees = 0f;
        }
    }

    private enum ValidationBucket
    {
        CENTER,
        LEFT,
        RIGHT,
        TOP,
        BOTTOM,
        X_TILT,
        Y_TILT,
        NEAR,
        FAR
    }

    private sealed class ValidationBucketStatistics
    {
        public readonly string Label;
        private readonly List<int> _markerCounts = new();
        private readonly List<int> _correspondenceCounts = new();
        private readonly List<float> _rmsValues = new();
        private readonly List<float> _maxResidualValues = new();
        private readonly List<Vector3> _stationaryPositions = new();
        private readonly List<Quaternion> _stationaryRotations = new();
        private Pose _lastObservedPose;
        private Pose _stationaryIntervalReferencePose;
        private bool _hasLastObservedPose;
        private int _settleSampleCount;

        public ValidationBucketStatistics(string label) => Label = label;

        public int AcceptedSamples => _rmsValues.Count;
        public float MeanRmsPixels => Mean(_rmsValues);

        public void Observe(
            NativeBoardResult result,
            Pose cameraFromBoard,
            int requiredSettleSamples,
            float maximumPositionDeltaMeters,
            float maximumRotationDeltaDegrees)
        {
            if (!_hasLastObservedPose ||
                Vector3.Distance(cameraFromBoard.position, _lastObservedPose.position) > maximumPositionDeltaMeters ||
                Quaternion.Angle(cameraFromBoard.rotation, _lastObservedPose.rotation) > maximumRotationDeltaDegrees ||
                Vector3.Distance(cameraFromBoard.position, _stationaryIntervalReferencePose.position) > maximumPositionDeltaMeters ||
                Quaternion.Angle(cameraFromBoard.rotation, _stationaryIntervalReferencePose.rotation) > maximumRotationDeltaDegrees)
            {
                _lastObservedPose = cameraFromBoard;
                _stationaryIntervalReferencePose = cameraFromBoard;
                _hasLastObservedPose = true;
                _settleSampleCount = 1;
                return;
            }

            _lastObservedPose = cameraFromBoard;
            _settleSampleCount++;
            if (_settleSampleCount <= requiredSettleSamples)
                return;

            _markerCounts.Add(result.markerCount);
            _correspondenceCounts.Add(result.correspondenceCount);
            _rmsValues.Add(result.reprojectionRms);
            _maxResidualValues.Add(result.maxResidual);
            _stationaryPositions.Add(cameraFromBoard.position);
            _stationaryRotations.Add(cameraFromBoard.rotation);
        }

        public void MarkInactive()
        {
            _hasLastObservedPose = false;
            _settleSampleCount = 0;
        }

        public string ToDiagnosticString() =>
            $"DJI_BOARD_VALIDATION_BUCKET_REPORT bucket={Label} acceptedSamples={AcceptedSamples} " +
            $"markerCountMean={Mean(_markerCounts):F2} markerCountMin={MinOrZero(_markerCounts)} " +
            $"correspondenceCountMean={Mean(_correspondenceCounts):F2} correspondenceCountMin={MinOrZero(_correspondenceCounts)} " +
            $"meanRmsPixels={Mean(_rmsValues):F3} medianRmsPixels={Median(_rmsValues):F3} p95RmsPixels={Percentile(_rmsValues, 0.95f):F3} maxRmsPixels={Max(_rmsValues):F3} " +
            $"meanMaxCornerResidualPixels={Mean(_maxResidualValues):F3} worstMaxCornerResidualPixels={Max(_maxResidualValues):F3} " +
            $"positionStdDevMeters={PositionStandardDeviation(_stationaryPositions):F5} rotationStdDevDegrees={RotationStandardDeviation(_stationaryRotations):F4} " +
            $"stationarySettleSamples={_settleSampleCount}";
    }

    [Serializable]
    public sealed class NativeBoardResult
    {
        public int frameWidth;
        public int frameHeight;
        // Legacy alias retained for compatibility. The explicit field below is
        // the arrival time captured at CameraFrameListener callback entry.
        public long timestampNs;
        public long visionFrameTimestampNs;
        public long frameArrivalTimestampNs;
        public long detectorStartTimestampNs;
        public long detectorEndTimestampNs;
        // The native call currently runs AprilTag detection and PnP together,
        // so this is its coupled completion time rather than a separate PnP callback.
        public long pnpCompletedTimestampNs;
        public string pnpTimingGranularity;
        public int frameSequence;
        public long listenerGeneration;
        public long visionFrameId;
        public string detectorFrameFormat;
        public int frameRotationDegrees;
        public string frameCrop;
        public string frameResize;
        public bool frameMirrorX;
        public bool calibrationUsable;
        public int status;
        public string rejectionReason;
        public int markerCount;
        public int cornerCount;
        public int correspondenceCount;
        public float reprojectionRms;
        public float maxResidual;
        public float[] cameraFromBoardPosition;
        public float[] cameraFromBoardRotationMatrix;
        // Transport schema: primitive flattened XY arrays. JsonUtility does not
        // reliably deserialize the previous float[][] jagged-corner payload.
        public int[] markerIds;
        public float[] markerDecisionMargins;
        public float[] detectedCornersXY;
        public float[] projectedCornersXY;
        public float[] reprojectionResiduals;

        [NonSerialized] public Marker[] markers;

        [Serializable]
        public sealed class Marker
        {
            public int id;
            public float decisionMargin;
            public float[][] detectedCorners;
            public float[][] projectedCorners;
        }
    }

    /// <summary>
    /// Timing for an authoritative visual pose. The source timestamp is CPU
    /// callback arrival time, not camera exposure time or an OES frame PTS.
    /// </summary>
    public readonly struct VisualPoseTimingSample
    {
        public readonly long sourceFrameId;
        public readonly long sourceFrameTimestampNs;
        public readonly long detectorStartTimestampNs;
        public readonly long detectorEndTimestampNs;
        public readonly long pnpCompletedTimestampNs;
        public readonly long poseDeliveredToUnityTimestampNs;
        public readonly Pose cameraFromBoard;
        public readonly int markerCount;
        public readonly float reprojectionRmsPixels;
        public readonly VisualPoseQuality quality;

        public bool IsValid => sourceFrameId > 0L && sourceFrameTimestampNs > 0L;

        public VisualPoseTimingSample(
            NativeBoardResult result,
            Pose cameraFromBoard,
            VisualPoseQuality quality,
            long poseDeliveredToUnityTimestampNs)
        {
            sourceFrameId = result.visionFrameId;
            sourceFrameTimestampNs = DjiBoardVisionProvider.GetVisionTimestamp(result);
            detectorStartTimestampNs = result.detectorStartTimestampNs;
            detectorEndTimestampNs = result.detectorEndTimestampNs;
            pnpCompletedTimestampNs = result.pnpCompletedTimestampNs != 0L
                ? result.pnpCompletedTimestampNs
                : result.detectorEndTimestampNs;
            this.poseDeliveredToUnityTimestampNs = poseDeliveredToUnityTimestampNs;
            this.cameraFromBoard = cameraFromBoard;
            markerCount = result.markerCount;
            reprojectionRmsPixels = result.reprojectionRms;
            this.quality = quality;
        }
    }

    private void Awake()
    {
        guardedHorizontalRenderConfiguration ??= new GuardedHorizontalRenderConfiguration();
        visualTemporalCompensationConfiguration ??= new VisualTemporalCompensationConfiguration();
        ApplyDroneViewStartupDefaults();
        _localizationSessionId = 0;
        _outageVisualGroundTruth.Reset();
        _calibration = GetComponent<DjiCameraCalibration>() ?? gameObject.AddComponent<DjiCameraCalibration>();
        _calibration.SetAllowProvisionalCalibrationForValidation(allowProvisionalCalibrationForValidation);
        if (rotationPropagationMode == RotationPropagationMode.Enabled &&
            !SafeForRotationOnlyPropagation &&
            !allowUnvalidatedRotationPropagationForDeveloperTesting)
        {
            rotationPropagationMode = RotationPropagationMode.ShadowDiagnostic;
            Debug.LogWarning(
                "DJI_ROTATION_PROPAGATION_BLOCKED reason=SAFETY_VALIDATION_REQUIRED " +
                "requested=Enabled applied=ShadowDiagnostic");
        }
        _telemetryProvider = FindFirstObjectByType<DjiCameraPoseProvider>();
        _telemetryValidationLogger = GetComponent<DjiTelemetryValidationLogger>() ??
                                      gameObject.AddComponent<DjiTelemetryValidationLogger>();
        _telemetryValidationLogger.Configure(this, _telemetryProvider);
        EnsureTelemetryRotationSubscription();
        Debug.Log(
            $"DJI_ROTATION_PROPAGATION_CAPABILITY mode={rotationPropagationMode} " +
            $"hasTelemetryOrientation={HasTelemetryOrientation} hasTelemetryPosition={HasTelemetryPosition} " +
            $"capability=ROTATION_ONLY_ZERO_TRANSLATION safeForRotationOnlyPropagation={SafeForRotationOnlyPropagation} " +
            $"developerOverride={allowUnvalidatedRotationPropagationForDeveloperTesting} renderingSource=VISUAL_ONLY");
        Debug.Log(
            "DJI_GUARDED_HORIZONTAL_CAPABILITY " +
            $"mode={horizontalTranslationRenderMode} minPropagationAgeMs={guardedHorizontalRenderConfiguration.minPropagationAgeMilliseconds:F0} " +
            $"maxPropagationAgeMs={guardedHorizontalRenderConfiguration.maxPropagationAgeMilliseconds:F0} " +
            $"maxGnssAgeMs={guardedHorizontalRenderConfiguration.maxGnssAgeMilliseconds:F0} " +
            $"enableThresholdM={guardedHorizontalRenderConfiguration.horizontalEnableThresholdMeters:F2} " +
            $"disableThresholdM={guardedHorizontalRenderConfiguration.horizontalDisableThresholdMeters:F2} " +
            $"requiredConsistentSamples={guardedHorizontalRenderConfiguration.requiredConsistentGnssSamples} " +
            "source=GNSS_ONLY velocityRendered=false hybridRendered=false verticalTranslation=UNCHANGED experimental=true");
        Debug.Log(
            "DJI_VISUAL_TEMPORAL_COMPENSATION_CAPABILITY " +
            $"mode={visualTemporalCompensationMode} maxPredictionMs={visualTemporalCompensationConfiguration.maxVisualPredictionMilliseconds:F0} " +
            $"maxOesTimingAgeMs={visualTemporalCompensationConfiguration.maxDisplayedTimingAgeMilliseconds:F0} " +
            "renderOnly=true authoritativeLocalization=UNCHANGED");
        Debug.Log(
            "DJI_PERSISTENT_WORLD_VISUAL_RENDER_CAPABILITY " +
            $"mode={visualRenderPath} visualOnly=true timestampMatchRequired=true " +
            "propagation=UNCHANGED");
        LogRotationConventionSanityCheck();
    }

    private IEnumerator Start()
    {
        ConfigureNativeCpuVision();
        RequestBoardVisionForVideoLifecycle();
        yield break;
    }

    private void Update()
    {
        EnsureTelemetryRotationSubscription();
        var nowTimestampNs = DjiBoardVisionBridge.GetMonotonicTimestampNs();
        ProcessPendingRotationDiagnosticSamples(nowTimestampNs);
        UpdateShadowPropagationState(rotationComparisonStaleTimeoutSeconds);
        if ((_hasRotationComparisonBaseline || _pendingRotationDiagnosticSamples.Count > 0) &&
            AcceptedCameraFromBoardAgeSeconds > rotationComparisonStaleTimeoutSeconds)
        {
            ResetRotationComparison("BOARD_POSE_STALE");
        }
        if ((_hasRotationComparisonBaseline || _pendingRotationDiagnosticSamples.Count > 0) &&
            _telemetryProvider != null &&
            !_telemetryProvider.HasTelemetryOrientation)
        {
            ResetRotationComparison("TELEMETRY_ORIENTATION_INVALID");
        }
        if (_hasShadowRotationPropagationBaseline &&
            _telemetryProvider != null &&
            !_telemetryProvider.HasTelemetryOrientation)
        {
            ResetShadowRotationPropagation("TELEMETRY_ORIENTATION_INVALID");
        }

        try
        {
            var json = DjiBoardVisionBridge.GetLatestResultJson();
            if (string.IsNullOrWhiteSpace(json))
                return;

            var result = JsonUtility.FromJson<NativeBoardResult>(json);
            if (result == null || result.frameSequence == _lastFrameSequence)
                return;

            _lastFrameSequence = result.frameSequence;
            LatestResult = result;
            Debug.Log($"DJI_BOARD_JSON_FETCH_OK frameSequence={result.frameSequence}");
            LogDeserializedResult(result);
            var diagnosticArrival = DjiBoardVisionBridge.GetMonotonicTimestampNs();
            BeginOrientationCandidate(result, diagnosticArrival);
            try { ProcessResult(result, diagnosticArrival); }
            finally { EndOrientationCandidate(result, diagnosticArrival); }
        }
        catch (Exception exception)
        {
            Debug.LogError($"DJI_BOARD_UNITY_CALLBACK_FAILED exception={exception.GetType().Name} message={exception.Message}");
        }
    }

    private void OnDestroy()
    {
        if (_telemetryProviderWithShadowSubscription != null)
        {
            _telemetryProviderWithShadowSubscription.TelemetryOrientationRecorded -= OnTelemetryOrientationRecorded;
            _telemetryProviderWithShadowSubscription.TelemetryVelocityRecorded -= OnTelemetryVelocityRecorded;
        }

        ResetShadowRotationPropagation("PROVIDER_DESTROYED");
        DjiBoardVisionBridge.Stop();
        if (_debugLineTexture != null)
            Destroy(_debugLineTexture);
    }

    private void OnDisable()
    {
        // A disabled provider must never resume a later DroneView session with
        // an old horizontal correction still armed or blended in.
        _worldLocalizationShadow?.ResetHorizontalGnssWorldShadowOutage();
        ResetGuardedHorizontalRenderState("PROVIDER_DISABLED", DjiBoardVisionBridge.GetMonotonicTimestampNs());
        _outageVisualGroundTruth.Reset();
        _hasPendingOutagePredictionCapture = false;
        _lastAuthoritativeVisualPose = default;
        _currentHighConfidenceVisualTimestampNs = RotationPropagationTiming.NoTimestamp;
        _authoritativeVisualTimingSample = default;
        _hasAuthoritativeVisualTimingSample = false;
        ResetVisualTemporalCompensationPredictor("PROVIDER_DISABLED");
    }

    private void OnGUI()
    {
        var result = LatestResult;
        if (!diagnosticLogging || result == null || result.markers == null || result.frameWidth <= 0 || result.frameHeight <= 0)
            return;

        foreach (var marker in result.markers)
        {
            DrawPixelQuad(marker.detectedCorners, result, Color.yellow);
            if (result.status == 2)
                DrawPixelQuad(marker.projectedCorners, result, Color.cyan);
        }

        if (result.status != 2 || !TryGetCameraFromBoard(result, out var cameraFromBoard))
            return;

        DrawBoardAxes(cameraFromBoard, result);
    }

    private void ConfigureNativeCpuVision()
    {
        if (!_calibration.CanRunBoardPoseDiagnostics)
        {
            Debug.LogWarning(
                $"DJI_PROVISIONAL_VALIDATION_DISABLED state={_calibration.CurrentValidationState} " +
                "reason=ALLOW_PROVISIONAL_CALIBRATION_FOR_VALIDATION_FALSE");
            return;
        }

        if (!ReferenceBoardDefinition.ValidateLongRangeV2Geometry(out var boardValidation))
        {
            Debug.LogError($"REFERENCE_BOARD_V2_INVALID {boardValidation}");
            return;
        }

        var layout = BuildNativeMarkerLayout(ReferenceBoardDefinition.Default.DjiFiducialMarkers);
        var data = _calibration.Current;
        try
        {
            DjiBoardVisionBridge.Configure(
                data.imageWidth,
                data.imageHeight,
                data.fx,
                data.fy,
                data.cx,
                data.cy,
                _calibration.DistortionCoefficients,
                layout,
                100);
        }
        catch (Exception exception)
        {
            Debug.LogError($"DJI_BOARD_JNI_CONFIGURE_FAILED exception={exception.GetType().Name} message={exception.Message}");
            return;
        }
        Debug.Log(
            $"DJI_REFERENCE_BOARD_LAYOUT widthMeters={ReferenceBoardDefinition.Default.WidthMeters:F3} heightMeters={ReferenceBoardDefinition.Default.HeightMeters:F3} " +
            $"markerCount={ReferenceBoardDefinition.Default.DjiFiducialMarkers.Count}");
        Debug.Log(
            $"REFERENCE_BOARD_VERSION={ReferenceBoardDefinition.Version}\n" +
            $"BOARD_SIZE_M={ReferenceBoardDefinition.PhysicalWidthMeters:F3}\n" +
            $"PRINTED_TAG_TILE_SIZE_M={ReferenceBoardDefinition.PrintedTagTileSizeMeters:F3}\n" +
            $"APRILTAG_DETECTION_SIZE_M={ReferenceBoardDefinition.AprilTagDetectionSizeMeters:F7}\n" +
            $"APRILTAG_WIDTH_AT_BORDER={ReferenceBoardDefinition.AprilTagWidthAtBorderModules}\n" +
            $"APRILTAG_TOTAL_WIDTH={ReferenceBoardDefinition.AprilTagTotalWidthModules}\n" +
            $"TAG_CENTER_OFFSET_M={ReferenceBoardDefinition.DjiMarkerCenterOffsetMeters:F3}\n" +
            $"TAG_CENTER_SPACING_M={ReferenceBoardDefinition.DjiMarkerCenterSpacingMeters:F3}\n" +
            "REFERENCE_BOARD_V2 boardSizeM=0.360 printedTagTileSizeM=0.170 aprilTagDetectionSizeM=0.094444 tagCenterOffsetM=0.090 outerMarginM=0.005 innerGapM=0.010 layout=APRILTAG_ONLY");
        Debug.Log(
            $"DJI_REFERENCE_BOARD_TAG_SIZE_AUDIT printedTileMeters={ReferenceBoardDefinition.PrintedTagTileSizeMeters:F6} " +
            $"widthAtBorderModules={ReferenceBoardDefinition.AprilTagWidthAtBorderModules} " +
            $"totalWidthModules={ReferenceBoardDefinition.AprilTagTotalWidthModules} " +
            $"nativePoseQuadrilateralMeters={ReferenceBoardDefinition.AprilTagDetectionSizeMeters:F6}");
        if (data.provisional)
        {
            var profileMessage = _calibration.CurrentValidationState == DjiCameraCalibration.ValidationState.UNVALIDATED
                ? "USING_UNVALIDATED_PUBLIC_FC3582_CALIBRATION"
                : "USING_REVIEWED_PUBLIC_FC3582_CALIBRATION";
            Debug.Log(
                profileMessage + " " +
                $"status={data.status} frame={data.imageWidth}x{data.imageHeight} " +
                $"worldInitializationAllowed={_calibration.CanInitializeWorld} " +
                $"allowProvisionalCalibrationForValidation={_calibration.AllowProvisionalCalibrationForValidation}");
        }
    }

    private void RequestBoardVisionForVideoLifecycle()
    {
        if (!_calibration.CanRunBoardPoseDiagnostics)
            return;

        try
        {
            if (DjiBoardVisionBridge.Start())
                Debug.Log("DJI_BOARD_CPU_VISION_STARTED source=VIDEO_LIFECYCLE_OWNER separateFromOes=true");
            else
                Debug.Log("DJI_BOARD_CPU_VISION_REQUESTED waitingForVideoLifecycle=true");
        }
        catch (Exception exception)
        {
            Debug.LogError($"DJI_BOARD_JNI_START_FAILED exception={exception.GetType().Name} message={exception.Message}");
        }
    }

    private void ProcessResult(NativeBoardResult result, long poseDeliveredToUnityTimestampNs)
    {
        ProcessResultWithQualityLossExperiment(result, poseDeliveredToUnityTimestampNs,
            DjiAprilTagNoMarkersQualityLossExperiment.Enabled);
    }

    private void ProcessResultWithQualityLossExperiment(
        NativeBoardResult result, long poseDeliveredToUnityTimestampNs, bool noMarkersExperimentEnabled)
    {
        if (TryHandleNoMarkersQualityLoss(result, poseDeliveredToUnityTimestampNs, noMarkersExperimentEnabled))
            return;

        var shouldLogDiagnosticSnapshot = ShouldLogDiagnosticSnapshot();
        var markerIds = result.markerIds == null
            ? string.Empty
            : string.Join(",", result.markerIds);
        if (shouldLogDiagnosticSnapshot)
        {
            Debug.Log(
                $"DJI_RUNTIME_FRAME width={result.frameWidth} height={result.frameHeight} " +
                $"format={result.detectorFrameFormat} source=DJI_IMAGEREADER_DETECTOR_INPUT");
            Debug.Log($"DJI_BOARD_MARKERS_VISIBLE count={result.markerCount} IDs={markerIds}");
            Debug.Log($"DJI_BOARD_CORNER_COUNT count={result.cornerCount}");
        }

        if (result.status < 0)
        {
            Reject($"NATIVE_FRAME_REJECTED status={result.status} reason={result.rejectionReason}");
            Debug.LogWarning($"DJI_BOARD_FRAME_REJECTED reason={result.rejectionReason} nativeStatus={result.status}");
            return;
        }

        if (result.markerCount == 0)
        {
            UpdateVisualPoseQuality(
                VisualPoseMetrics.Unavailable(GetVisionTimestamp(result)),
                "NO_MARKERS_DETECTED");
            State = LocalizationState.WaitingForReference;
            return;
        }

        if (!TryBuildDiagnosticMarkers(result, out var diagnosticReason))
        {
            // Diagnostic overlays must not discard an otherwise valid board pose.
            Debug.LogWarning($"BOARD_DIAGNOSTIC_DATA_INVALID frameId={result.visionFrameId} reason={diagnosticReason}");
        }

        if (!ValidateNativeResult(result, out var validationReason))
        {
            Reject($"UNITY_RESULT_VALIDATION_FAILED reason={validationReason}");
            Debug.LogWarning($"DJI_BOARD_FRAME_REJECTED reason={validationReason}");
            return;
        }

        if (!_calibration.TryValidateRuntimeFrameContract(
                result.frameWidth,
                result.frameHeight,
                result.detectorFrameFormat,
                result.frameRotationDegrees,
                result.frameCrop,
                result.frameResize,
                result.frameMirrorX,
                out var frameMismatch))
        {
            Reject($"CALIBRATION_MISMATCH reason={frameMismatch}");
            return;
        }

        if (!result.calibrationUsable)
        {
            Reject("CALIBRATION_UNUSABLE reason=NATIVE_CALIBRATION_REJECTED");
            return;
        }

        Debug.Log(
            $"CALIBRATION_FRAME_MATCH runtimeFormat={result.detectorFrameFormat} " +
            $"calibrationFormat={_calibration.Current.detectorFrameFormat} width={result.frameWidth} height={result.frameHeight} " +
            $"rotation={result.frameRotationDegrees} crop={result.frameCrop} resize={result.frameResize} mirror={result.frameMirrorX}");

        if (!TryGetCameraFromBoard(result, out var cameraFromBoard))
        {
            Reject("NATIVE_BOARD_POSE_UNAVAILABLE");
            return;
        }

        Debug.Log($"DJI_BOARD_RESULT_ACCEPTED frameId={result.visionFrameId} markerCount={result.markerCount} correspondenceCount={result.correspondenceCount} rms={result.reprojectionRms:F3}");
        if (shouldLogDiagnosticSnapshot)
        {
            LogVisionTimestamp(result, DjiBoardVisionBridge.GetMonotonicTimestampNs());
            _telemetryProvider?.LogVelocityClockDomainComparison(GetVisionTimestamp(result));
        }

        var visualPoseMetrics = BuildVisualPoseMetrics(result, cameraFromBoard, markerIds);
        _orientationMetrics = visualPoseMetrics;
        RecordVisualPoseTransitionSample(visualPoseMetrics);
        UpdateVisualPoseQuality(visualPoseMetrics, EvaluateVisualPoseQuality(visualPoseMetrics));

        // A native status=2 result has passed frame-contract and calibration
        // validation. Keep it through short detector gaps for preview only;
        // this does not alter DJI_WORLD_INITIALIZED semantics.
        CurrentCameraFromBoard = cameraFromBoard;
        HasAcceptedCameraFromBoard = true;
        _lastAcceptedCameraFromBoardRealtime = Time.realtimeSinceStartup;
        if (shouldLogDiagnosticSnapshot)
            Debug.Log($"DJI_BOARD_POSE_ACCEPTED ageMs=0 frameId={result.visionFrameId}");

        if (shouldLogDiagnosticSnapshot)
        {
            Debug.Log($"DJI_BOARD_POSE_ESTIMATED T_CAMERA_BOARD position={cameraFromBoard.position} rotation={cameraFromBoard.rotation.eulerAngles}");
            Debug.Log($"DJI_BOARD_REPROJECTION_RMS rmsPixels={result.reprojectionRms:F3}");
            Debug.Log($"DJI_BOARD_MAX_CORNER_ERROR pixels={result.maxResidual:F3}");
        }
        if (CurrentVisualPoseQuality == VisualPoseQuality.HighConfidence)
        {
            if (RequiresVisualReacquisitionHandover())
            {
                ObserveVisualReacquisitionCandidate(
                    result,
                    visualPoseMetrics,
                    shouldLogDiagnosticSnapshot);
            }
            else if (_holdingVisualAuthorityForTranslationJumpSuspect || _holdingVisualAuthorityForBorderlineQuality)
            {
                // Borderline four-marker failures must not replace either the
                // rendered visual pose or the propagation baseline until they
                // are confirmed across consecutive frames.
                _lastHighConfidenceCameraFromBoardRealtime = Time.realtimeSinceStartup;
                if (shouldLogDiagnosticSnapshot)
                {
                    var holdReason = _holdingVisualAuthorityForBorderlineQuality
                        ? "BORDERLINE_VISUAL_QUALITY_HOLDING_VISUAL_AUTHORITY"
                        : "TRANSLATION_JUMP_SUSPECT_HOLDING_VISUAL_AUTHORITY";
                    Debug.Log($"DJI_ROTATION_SYNC skipped={holdReason}");
                }
            }
            else
            {
                PromoteHighConfidenceVisualPose(
                    result,
                    visualPoseMetrics,
                    shouldLogDiagnosticSnapshot,
                    poseDeliveredToUnityTimestampNs);
            }
        }
        else if (shouldLogDiagnosticSnapshot)
        {
            Debug.Log("DJI_ROTATION_SYNC skipped=VISUAL_RENDER_QUALITY_GATE");
        }
        RecordProvisionalDiagnostics(result, cameraFromBoard, shouldLogDiagnosticSnapshot);

        State = LocalizationState.Localizing;
    }

    private bool PassesImageQuality(NativeBoardResult result)
    {
        return result.markerCount >= minimumVisibleMarkers &&
               result.cornerCount >= minimumCorners &&
               float.IsFinite(result.reprojectionRms) && result.reprojectionRms <= maximumReprojectionRmsPixels &&
               float.IsFinite(result.maxResidual) && result.maxResidual <= maximumResidualPixels;
    }

    private VisualPoseMetrics BuildVisualPoseMetrics(
        NativeBoardResult result,
        Pose cameraFromBoard,
        string markerIds)
    {
        var minimumBorderDistancePixels = float.PositiveInfinity;
        var minimum = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
        var maximum = new Vector2(float.NegativeInfinity, float.NegativeInfinity);

        for (var index = 0; index < result.detectedCornersXY.Length; index += 2)
        {
            var x = result.detectedCornersXY[index];
            var y = result.detectedCornersXY[index + 1];
            minimumBorderDistancePixels = Mathf.Min(
                minimumBorderDistancePixels,
                x,
                y,
                result.frameWidth - x,
                result.frameHeight - y);
            minimum = Vector2.Min(minimum, new Vector2(x, y));
            maximum = Vector2.Max(maximum, new Vector2(x, y));
        }

        var detectedBoundsAreaPixels = Mathf.Max(0f, maximum.x - minimum.x) * Mathf.Max(0f, maximum.y - minimum.y);
        var observedMarkerSizePixels = CalculateObservedMarkerSizePixels(result);
        var normalizedReprojectionRms = DjiVisualPoseQualityPolicy.NormalizePixelResidual(
            result.reprojectionRms,
            observedMarkerSizePixels);
        var normalizedMaxCornerResidual = DjiVisualPoseQualityPolicy.NormalizePixelResidual(
            result.maxResidual,
            observedMarkerSizePixels);
        var timestampNs = GetVisionTimestamp(result);
        var translationJumpMeters = _hasLastVisualQualityPose
            ? Vector3.Distance(cameraFromBoard.position, _lastVisualQualityPose.position)
            : 0f;
        var rotationJumpDegrees = _hasLastVisualQualityPose
            ? Quaternion.Angle(cameraFromBoard.rotation, _lastVisualQualityPose.rotation)
            : 0f;

        var visualPoseDeltaSeconds = _hasLastVisualQualityPose &&
                                      timestampNs > _lastVisualQualityPoseTimestampNs
            ? (float)((timestampNs - _lastVisualQualityPoseTimestampNs) / 1_000_000_000.0)
            : 0f;
        var hasTelemetryRotationAtVision = TryInterpolateTelemetryRotation(
            timestampNs,
            out var telemetryAtVision,
            out _, "VISUAL_QUALITY");
        var hasTelemetryRotationPrediction = false;
        var rotationCompensatedTranslationResidualMeters = float.NaN;
        if (_hasLastVisualQualityPose && _hasLastVisualQualityTelemetryRotation && hasTelemetryRotationAtVision)
        {
            var cameraTFromCamera0 = ReferenceFrameTransforms.CameraTFromCamera0Rotation(
                _lastVisualQualityTelemetryRotation,
                telemetryAtVision.navigationFromCameraRotation);
            var expectedCameraFromBoardPosition = cameraTFromCamera0 * _lastVisualQualityPose.position;
            rotationCompensatedTranslationResidualMeters = Vector3.Distance(
                cameraFromBoard.position,
                expectedCameraFromBoardPosition);
            hasTelemetryRotationPrediction = true;
        }

        if (hasTelemetryRotationAtVision)
        {
            _lastVisualQualityTelemetryRotation = telemetryAtVision.navigationFromCameraRotation;
            _hasLastVisualQualityTelemetryRotation = true;
        }

        var telemetrySpeedMetersPerSecond = float.NaN;
        var hasTelemetrySpeed = _telemetryProvider != null &&
                                _telemetryProvider.TryGetAircraftVelocityMagnitudeMetersPerSecond(out telemetrySpeedMetersPerSecond);
        _lastVisualQualityPose = cameraFromBoard;
        _hasLastVisualQualityPose = true;
        _lastVisualQualityPoseTimestampNs = timestampNs;

        return new VisualPoseMetrics(
            timestampNs,
            result.markerCount,
            result.correspondenceCount,
            markerIds,
            result.reprojectionRms,
            result.maxResidual,
            observedMarkerSizePixels,
            normalizedReprojectionRms,
            normalizedMaxCornerResidual,
            minimumBorderDistancePixels,
            detectedBoundsAreaPixels,
            cameraFromBoard,
            translationJumpMeters,
            rotationJumpDegrees,
            visualPoseDeltaSeconds,
            hasTelemetryRotationPrediction,
            rotationCompensatedTranslationResidualMeters,
            hasTelemetrySpeed,
            hasTelemetrySpeed ? telemetrySpeedMetersPerSecond : float.NaN);
    }

    private VisualPoseQualityEvaluation EvaluateVisualPoseQuality(VisualPoseMetrics metrics)
    {
        return EvaluateVisualPoseQualityForBorderProfile(metrics, DjiAprilTagBorderExperiment.Enabled);
    }

    private VisualPoseQualityEvaluation EvaluateVisualPoseQualityForBorderProfile(
        VisualPoseMetrics metrics, bool borderExperimentEnabled)
    {
        return EvaluateVisualPoseQualityForExperiments(metrics, borderExperimentEnabled,
            DjiAprilTagStaleJumpExperiment.Enabled);
    }

    private VisualPoseQualityEvaluation EvaluateLegacyVisualPoseQualityForBorderProfile(
        VisualPoseMetrics metrics, bool borderExperimentEnabled, bool retentionExperimentEnabled)
    {
        var minimumRequiredBorderDistancePixels = DjiAprilTagBorderExperiment.Resolve(
            highConfidenceMinimumBorderDistancePixels, metrics.markerCount,
            IsBorderExperimentReacquisition(), borderExperimentEnabled);
        minimumRequiredBorderDistancePixels = DjiAprilTagBorderRetentionExperiment.Resolve(
            minimumRequiredBorderDistancePixels, metrics.markerCount, metrics.minimumBorderDistancePixels,
            CurrentRenderPoseSource, CurrentVisualPoseQuality == VisualPoseQuality.HighConfidence, retentionExperimentEnabled);
        RecordBorderRetentionEvaluation(metrics, minimumRequiredBorderDistancePixels, retentionExperimentEnabled);
        _latestTranslationJumpEvaluation = default;
        if (metrics.markerCount == 4)
        {
            var scaleAwareReason = DjiVisualPoseQualityPolicy.EvaluateFourMarkerScaleAware(
                metrics.reprojectionRmsPixels,
                metrics.maxCornerResidualPixels,
                metrics.observedMarkerSizePixels,
                highConfidenceFourMarkerMaximumNormalizedRms,
                highConfidenceFourMarkerMaximumNormalizedCornerResidual,
                highConfidenceFourMarkerHardMaximumReprojectionRmsPixels,
                highConfidenceFourMarkerHardMaximumCornerResidualPixels);
            var nonTranslationReason = DjiVisualPoseQualityPolicy.Evaluate(
                metrics.markerCount,
                metrics.reprojectionRmsPixels,
                metrics.maxCornerResidualPixels,
                metrics.minimumBorderDistancePixels,
                0f,
                _hasLastVisualQualityPose ? metrics.rotationJumpDegrees : 0f,
                allowSingleMarkerHighConfidence,
                highConfidenceMaximumReprojectionRmsPixels,
                float.PositiveInfinity,
                highConfidenceMaximumCornerResidualPixels,
                float.PositiveInfinity,
                minimumRequiredBorderDistancePixels,
                highConfidenceMaximumFrameTranslationJumpMeters,
                highConfidenceMaximumFrameRotationJumpDegrees,
                evaluateTranslationJump: false,
                evaluateFourMarkerPixelResiduals: false);

            var borderlineCorner = scaleAwareReason == "NORMALIZED_CORNER_RESIDUAL" &&
                                   DjiVisualPoseQualityPolicy.IsBorderlineFourMarkerNormalizedCornerResidual(
                                       metrics.normalizedMaxCornerResidual,
                                       highConfidenceFourMarkerMaximumNormalizedCornerResidual,
                                       highConfidenceFourMarkerBorderlineNormalizedCornerResidual);
            var borderlineBorder = nonTranslationReason == "BORDER_PROXIMITY" &&
                                   DjiVisualPoseQualityPolicy.IsBorderlineFourMarkerBorderProximity(
                                       metrics.minimumBorderDistancePixels,
                                        minimumRequiredBorderDistancePixels,
                                       highConfidenceFourMarkerSevereBorderDistancePixels);
            var severeRotationJump = !float.IsFinite(metrics.rotationJumpDegrees) ||
                                     metrics.rotationJumpDegrees > highConfidenceMaximumFrameRotationJumpDegrees;

            if ((!string.IsNullOrEmpty(scaleAwareReason) && !borderlineCorner) ||
                (!string.IsNullOrEmpty(nonTranslationReason) && !borderlineBorder) ||
                severeRotationJump)
            {
                return new VisualPoseQualityEvaluation(
                    !string.IsNullOrEmpty(scaleAwareReason) && !borderlineCorner
                        ? scaleAwareReason
                        : severeRotationJump ? "POSE_ROTATION_JUMP" : nonTranslationReason,
                    FourMarkerVisualQualityFailureSeverity.Severe);
            }

            if (borderlineCorner || borderlineBorder)
            {
                return new VisualPoseQualityEvaluation(
                    borderlineCorner ? scaleAwareReason : nonTranslationReason,
                    FourMarkerVisualQualityFailureSeverity.Borderline);
            }

            _latestTranslationJumpEvaluation = DjiVisualPoseQualityPolicy.EvaluateMotionAwareTranslationJump(
                metrics.translationJumpMeters,
                metrics.visualPoseDeltaSeconds,
                metrics.hasTelemetryRotationPrediction,
                metrics.rotationCompensatedTranslationResidualMeters,
                metrics.hasTelemetrySpeed,
                metrics.telemetrySpeedMetersPerSecond,
                highConfidenceMaximumFrameTranslationJumpMeters,
                motionAwareImmediateTranslationJumpRejectMeters,
                motionAwareTranslationResidualAllowanceMeters,
                motionAwareTelemetrySpeedAllowanceMultiplier);
            return _latestTranslationJumpEvaluation.decision == VisualTranslationJumpDecision.Reject
                ? new VisualPoseQualityEvaluation(
                    "POSE_TRANSLATION_JUMP_SEVERE",
                    FourMarkerVisualQualityFailureSeverity.Severe)
                : VisualPoseQualityEvaluation.Pass;
        }

        var lowerMarkerReason = DjiVisualPoseQualityPolicy.Evaluate(
            metrics.markerCount,
            metrics.reprojectionRmsPixels,
            metrics.maxCornerResidualPixels,
            metrics.minimumBorderDistancePixels,
            _hasLastVisualQualityPose ? metrics.translationJumpMeters : 0f,
            _hasLastVisualQualityPose ? metrics.rotationJumpDegrees : 0f,
            allowSingleMarkerHighConfidence,
            highConfidenceMaximumReprojectionRmsPixels,
            float.PositiveInfinity,
            highConfidenceMaximumCornerResidualPixels,
            float.PositiveInfinity,
            minimumRequiredBorderDistancePixels,
            highConfidenceMaximumFrameTranslationJumpMeters,
            highConfidenceMaximumFrameRotationJumpDegrees,
            evaluateTranslationJump: true,
            evaluateFourMarkerPixelResiduals: false);
        return string.IsNullOrEmpty(lowerMarkerReason)
            ? VisualPoseQualityEvaluation.Pass
            : new VisualPoseQualityEvaluation(lowerMarkerReason, FourMarkerVisualQualityFailureSeverity.Severe);
    }

    private string GetVisualQualityThresholdSummary(VisualPoseMetrics metrics)
    {
        if (metrics.markerCount == 4)
        {
            return $"normalizedRmsThreshold={highConfidenceFourMarkerMaximumNormalizedRms:F3} " +
                   $"normalizedCornerThreshold={highConfidenceFourMarkerMaximumNormalizedCornerResidual:F3} " +
                   $"hardRmsCapPx={highConfidenceFourMarkerHardMaximumReprojectionRmsPixels:F1} " +
                   $"hardCornerCapPx={highConfidenceFourMarkerHardMaximumCornerResidualPixels:F1}";
        }

        return $"rmsThreshold={highConfidenceMaximumReprojectionRmsPixels:F1} " +
               $"cornerResidualThreshold={highConfidenceMaximumCornerResidualPixels:F1}";
    }

    private void UpdateVisualPoseQuality(VisualPoseMetrics metrics, string degradationReason)
    {
        UpdateVisualPoseQuality(
            metrics,
            string.IsNullOrEmpty(degradationReason)
                ? VisualPoseQualityEvaluation.Pass
                : new VisualPoseQualityEvaluation(degradationReason, FourMarkerVisualQualityFailureSeverity.Severe));
    }

    private void UpdateVisualPoseQuality(VisualPoseMetrics metrics, VisualPoseQualityEvaluation evaluation)
    {
        if (metrics.markerCount <= 0) InterruptStaleJumpCluster("STALE_CLUSTER_VISUAL_UNAVAILABLE");
        _orientationDiagnosticQualityReason = evaluation.reason;
        if (_hasVisualReacquisitionCandidate &&
            (metrics.markerCount <= 0 ||
             !string.IsNullOrEmpty(evaluation.reason) ||
             _latestTranslationJumpEvaluation.decision == VisualTranslationJumpDecision.Suspect))
        {
            var cancellationReason = metrics.markerCount <= 0
                ? "VISUAL_POSE_UNAVAILABLE"
                : !string.IsNullOrEmpty(evaluation.reason)
                    ? evaluation.reason
                    : "POSE_TRANSLATION_JUMP_SUSPECT";
            CancelVisualReacquisition(cancellationReason);
        }

        if (metrics.markerCount <= 0)
        {
            _consecutiveHighConfidenceVisualPoses = 0;
            ResetBorderlineFourMarkerQualityFailure();
            ResetTranslationJumpSuspect();
            SetVisualPoseQuality(VisualPoseQuality.Unavailable, metrics, evaluation.reason);
            return;
        }

        if (!string.IsNullOrEmpty(evaluation.reason))
        {
            _consecutiveHighConfidenceVisualPoses = 0;
            if (evaluation.severity == FourMarkerVisualQualityFailureSeverity.Borderline)
            {
                ResetTranslationJumpSuspect(metrics);
                _consecutiveBorderlineFourMarkerQualityFailures =
                    DjiVisualPoseQualityPolicy.AdvanceBorderlineFailureCount(
                        _consecutiveBorderlineFourMarkerQualityFailures,
                        evaluation.severity);

                var hasVisualAuthority = CurrentVisualPoseQuality == VisualPoseQuality.HighConfidence;
                var confirmed = DjiVisualPoseQualityPolicy.IsBorderlineFailureConfirmed(
                    _consecutiveBorderlineFourMarkerQualityFailures,
                    fourMarkerBorderlineQualityConfirmationFrames);
                if (DjiVisualPoseQualityPolicy.ShouldHoldVisualAuthorityForBorderlineFailure(
                        hasVisualAuthority,
                        _consecutiveBorderlineFourMarkerQualityFailures,
                        fourMarkerBorderlineQualityConfirmationFrames))
                {
                    _holdingVisualAuthorityForBorderlineQuality = true;
                    if (_consecutiveBorderlineFourMarkerQualityFailures == 1)
                    {
                        LogBorderlineFourMarkerQualityHysteresis(
                            evaluation.reason,
                            "BORDERLINE",
                            "HOLD_VISUAL");
                    }
                    return;
                }

                _holdingVisualAuthorityForBorderlineQuality = false;
                if (hasVisualAuthority)
                {
                    LogBorderlineFourMarkerQualityHysteresis(
                        evaluation.reason,
                        "BORDERLINE",
                        "DEGRADE");
                }

                SetVisualPoseQuality(
                    VisualPoseQuality.Degraded,
                    metrics,
                    confirmed ? $"{evaluation.reason}_CONFIRMED" : evaluation.reason);
                return;
            }

            ResetBorderlineFourMarkerQualityFailure();
            if (_latestTranslationJumpEvaluation.decision == VisualTranslationJumpDecision.Reject)
                LogTranslationJumpDecisionIfChanged(metrics, VisualTranslationJumpDecision.Reject);
            ResetTranslationJumpSuspect(metrics);
            SetVisualPoseQuality(VisualPoseQuality.Degraded, metrics, evaluation.reason);
            return;
        }

        ResetBorderlineFourMarkerQualityFailure();

        if (metrics.markerCount == 4 && _latestTranslationJumpEvaluation.decision == VisualTranslationJumpDecision.Suspect)
        {
            _consecutiveTranslationJumpSuspects++;
            LogTranslationJumpDecisionIfChanged(metrics, VisualTranslationJumpDecision.Suspect);
            if (DjiVisualPoseQualityPolicy.IsTranslationJumpConfirmed(
                    _consecutiveTranslationJumpSuspects,
                    motionAwareTranslationJumpConfirmationFrames))
            {
                _consecutiveHighConfidenceVisualPoses = 0;
                _holdingVisualAuthorityForTranslationJumpSuspect = false;
                SetVisualPoseQuality(VisualPoseQuality.Degraded, metrics, "POSE_TRANSLATION_JUMP_CONFIRMED");
                return;
            }

            _holdingVisualAuthorityForTranslationJumpSuspect = CurrentVisualPoseQuality == VisualPoseQuality.HighConfidence;
            if (_holdingVisualAuthorityForTranslationJumpSuspect)
                return;

            _consecutiveHighConfidenceVisualPoses = 0;
            SetVisualPoseQuality(
                VisualPoseQuality.Degraded,
                metrics,
                $"POSE_TRANSLATION_JUMP_SUSPECT_NO_VISUAL_AUTHORITY_{_consecutiveTranslationJumpSuspects}/{motionAwareTranslationJumpConfirmationFrames}");
            return;
        }

        ResetTranslationJumpSuspect(metrics);

        if (CurrentVisualPoseQuality == VisualPoseQuality.HighConfidence)
        {
            _consecutiveHighConfidenceVisualPoses = highConfidenceReacquisitionConfirmationFrames;
            return;
        }

        _consecutiveHighConfidenceVisualPoses++;
        if (_consecutiveHighConfidenceVisualPoses < highConfidenceReacquisitionConfirmationFrames)
        {
            SetVisualPoseQuality(
                VisualPoseQuality.Degraded,
                metrics,
                $"REACQUISITION_CONFIRMING_{_consecutiveHighConfidenceVisualPoses}/{highConfidenceReacquisitionConfirmationFrames}");
            return;
        }

        SetVisualPoseQuality(VisualPoseQuality.HighConfidence, metrics, "HIGH_CONFIDENCE_CRITERIA_MET");
    }

    private void ResetTranslationJumpSuspect(VisualPoseMetrics? metrics = null)
    {
        if (_consecutiveTranslationJumpSuspects > 0 && metrics.HasValue)
            LogTranslationJumpDecisionIfChanged(metrics.Value, VisualTranslationJumpDecision.Accept);

        _consecutiveTranslationJumpSuspects = 0;
        _holdingVisualAuthorityForTranslationJumpSuspect = false;
    }

    private void ResetBorderlineFourMarkerQualityFailure()
    {
        _consecutiveBorderlineFourMarkerQualityFailures = 0;
        _holdingVisualAuthorityForBorderlineQuality = false;
    }

    private void LogBorderlineFourMarkerQualityHysteresis(
        string reason,
        string severity,
        string decision)
    {
        Debug.Log(
            "DJI_VISUAL_QUALITY_HYSTERESIS " +
            $"reason={reason} severity={severity} " +
            $"consecutiveFailures={_consecutiveBorderlineFourMarkerQualityFailures} " +
            $"decision={decision}");
    }

    private void LogTranslationJumpDecisionIfChanged(
        VisualPoseMetrics metrics,
        VisualTranslationJumpDecision decision)
    {
        if (_lastLoggedTranslationJumpDecision == decision)
            return;

        _lastLoggedTranslationJumpDecision = decision;
        var evaluation = _latestTranslationJumpEvaluation;
        var telemetrySpeed = float.IsFinite(evaluation.telemetrySpeedMetersPerSecond)
            ? evaluation.telemetrySpeedMetersPerSecond.ToString("F3")
            : "UNAVAILABLE";
        Debug.Log(
            "DJI_VISUAL_TRANSLATION_JUMP " +
            $"deltaM={metrics.translationJumpMeters:F3} dtMs={metrics.visualPoseDeltaSeconds * 1000f:F1} " +
            $"impliedSpeedMps={evaluation.impliedSpeedMetersPerSecond:F3} telemetrySpeedMps={telemetrySpeed} " +
            $"rotationCompensatedResidualM={evaluation.rotationCompensatedResidualMeters:F3} " +
            $"decision={decision.ToString().ToUpperInvariant()}");
    }

    private void SetVisualPoseQuality(
        VisualPoseQuality quality,
        VisualPoseMetrics metrics,
        string reason)
    {
        _orientationDiagnosticQualityReason = reason;
        if (CurrentVisualPoseQuality == quality)
            return;

        var previousQuality = CurrentVisualPoseQuality;
        CurrentVisualPoseQuality = quality;
        var scaleDecision = quality == VisualPoseQuality.HighConfidence ? "ACCEPT" : reason;
        if (metrics.markerCount == 4)
        {
            Debug.Log(
                "DJI_VISUAL_SCALE_QUALITY " +
                $"markerCount=4 observedMarkerSizePx={metrics.observedMarkerSizePixels:F3} " +
                $"rmsPx={metrics.reprojectionRmsPixels:F3} normalizedRms={metrics.normalizedReprojectionRms:F4} " +
                $"maxCornerPx={metrics.maxCornerResidualPixels:F3} normalizedMaxCorner={metrics.normalizedMaxCornerResidual:F4} " +
                $"decision={scaleDecision}");
        }
        Debug.Log(
            "DJI_VISUAL_POSE_QUALITY " +
            $"state={FormatVisualPoseQuality(quality)} markerCount={metrics.markerCount} " +
            $"rms={metrics.reprojectionRmsPixels:F3} maxCornerResidual={metrics.maxCornerResidualPixels:F3} " +
            $"{GetVisualQualityThresholdSummary(metrics)} " +
            $"borderDistancePx={metrics.minimumBorderDistancePixels:F1} " +
            $"translationJumpM={metrics.translationJumpMeters:F3} rotationJumpDeg={metrics.rotationJumpDegrees:F2} " +
            $"reason={reason}");

        if (previousQuality == VisualPoseQuality.HighConfidence && quality != VisualPoseQuality.HighConfidence)
        {
            ResetAircraftVelocityVisualValidation("VISUAL_QUALITY_INTERRUPTED");
            LogVisualPoseTransitionHistory(reason);
        }
    }

    private void RecordVisualPoseTransitionSample(VisualPoseMetrics metrics)
    {
        if (_visualPoseTransitionHistory.Count == VisualPoseTransitionHistoryCapacity)
            _visualPoseTransitionHistory.Dequeue();

        _visualPoseTransitionHistory.Enqueue(metrics);
    }

    private void LogVisualPoseTransitionHistory(string lossReason)
    {
        Debug.Log($"DJI_VISUAL_POSE_TRANSITION_WINDOW reason={lossReason} samples={_visualPoseTransitionHistory.Count}");
        foreach (var metrics in _visualPoseTransitionHistory)
        {
            Debug.Log(
                "DJI_VISUAL_POSE_TRANSITION_FRAME " +
                $"timestampNs={metrics.timestampNs} markerCount={metrics.markerCount} " +
                $"correspondenceCount={metrics.correspondenceCount} markerIds={metrics.markerIds} " +
                $"rms={metrics.reprojectionRmsPixels:F3} maxCornerResidual={metrics.maxCornerResidualPixels:F3} " +
                $"borderDistancePx={metrics.minimumBorderDistancePixels:F1} boundsAreaPx={metrics.detectedBoundsAreaPixels:F1} " +
                $"cameraPosition={metrics.cameraFromBoard.position} cameraRotation={metrics.cameraFromBoard.rotation.eulerAngles} " +
                $"translationJumpM={metrics.translationJumpMeters:F3} rotationJumpDeg={metrics.rotationJumpDegrees:F2}");
        }
    }

    private static float CalculateObservedMarkerSizePixels(NativeBoardResult result)
    {
        if (result.markerCount != 4 || result.detectedCornersXY == null || result.detectedCornersXY.Length != 32)
            return float.NaN;

        var markerSizes = new float[4];
        for (var markerIndex = 0; markerIndex < 4; markerIndex++)
        {
            var edgeLengthSum = 0f;
            for (var cornerIndex = 0; cornerIndex < 4; cornerIndex++)
            {
                var nextCornerIndex = (cornerIndex + 1) % 4;
                var cornerOffset = (markerIndex * 4 + cornerIndex) * 2;
                var nextCornerOffset = (markerIndex * 4 + nextCornerIndex) * 2;
                var corner = new Vector2(
                    result.detectedCornersXY[cornerOffset],
                    result.detectedCornersXY[cornerOffset + 1]);
                var nextCorner = new Vector2(
                    result.detectedCornersXY[nextCornerOffset],
                    result.detectedCornersXY[nextCornerOffset + 1]);
                var edgeLength = Vector2.Distance(corner, nextCorner);
                if (!float.IsFinite(edgeLength) || edgeLength <= 0f)
                    return float.NaN;

                edgeLengthSum += edgeLength;
            }

            markerSizes[markerIndex] = edgeLengthSum / 4f;
        }

        return Median(markerSizes);
    }

    private static string FormatVisualPoseQuality(VisualPoseQuality quality) => quality switch
    {
        VisualPoseQuality.HighConfidence => "HIGH_CONFIDENCE",
        VisualPoseQuality.Degraded => "DEGRADED",
        _ => "UNAVAILABLE"
    };

    private bool TryGetCameraFromBoard(NativeBoardResult result, out Pose cameraFromBoard)
    {
        cameraFromBoard = Pose.identity;
        if (result.status != 2 || result.cameraFromBoardPosition?.Length != 3 || result.cameraFromBoardRotationMatrix?.Length != 9)
            return false;

        var rotation = result.cameraFromBoardRotationMatrix;
        for (var index = 0; index < rotation.Length; index++)
        {
            if (!float.IsFinite(rotation[index]))
                return false;
        }
        var matrix = Matrix4x4.identity;
        matrix.m00 = rotation[0]; matrix.m01 = rotation[1]; matrix.m02 = rotation[2];
        matrix.m10 = rotation[3]; matrix.m11 = rotation[4]; matrix.m12 = rotation[5];
        matrix.m20 = rotation[6]; matrix.m21 = rotation[7]; matrix.m22 = rotation[8];
        matrix.m03 = result.cameraFromBoardPosition[0];
        matrix.m13 = result.cameraFromBoardPosition[1];
        matrix.m23 = result.cameraFromBoardPosition[2];
        cameraFromBoard = ReferenceFrameTransforms.PoseFromMatrix(matrix);
        return float.IsFinite(cameraFromBoard.position.x) && float.IsFinite(cameraFromBoard.position.y) && float.IsFinite(cameraFromBoard.position.z);
    }

    public bool TryGetAcceptedCameraFromBoard(out Pose cameraFromBoard, out float ageSeconds)
    {
        cameraFromBoard = CurrentCameraFromBoard;
        ageSeconds = AcceptedCameraFromBoardAgeSeconds;
        return HasAcceptedCameraFromBoard;
    }

    private static bool ValidateNativeResult(NativeBoardResult result, out string reason)
    {
        reason = string.Empty;
        if (result.markerCount <= 0 || result.markerIds == null || result.markerIds.Length != result.markerCount)
        {
            reason = "MARKER_ID_COUNT_MISMATCH";
            return false;
        }

        if (result.correspondenceCount != result.cornerCount || result.correspondenceCount != result.markerCount * 4 || result.correspondenceCount < 4)
        {
            reason = $"CORRESPONDENCE_COUNT_MISMATCH markerCount={result.markerCount} correspondenceCount={result.correspondenceCount} cornerCount={result.cornerCount}";
            return false;
        }

        foreach (var markerId in result.markerIds)
        {
            if (!ReferenceBoardDefinition.Default.TryGetDjiFiducialMarker(markerId.ToString(), out _))
            {
                reason = "MARKER_ID_NOT_IN_REFERENCE_BOARD_DEFINITION";
                return false;
            }
        }

        return true;
    }

    private static bool TryBuildDiagnosticMarkers(NativeBoardResult result, out string reason)
    {
        reason = string.Empty;
        var expectedCornerFloatCount = result.correspondenceCount * 2;
        if (result.markerIds == null)
        {
            reason = "MARKER_CORNERS_INVALID reason=NULL_MARKER_IDS";
            return false;
        }
        if (result.markerDecisionMargins == null)
        {
            reason = "MARKER_CORNERS_INVALID reason=NULL_DECISION_MARGIN_ARRAY";
            return false;
        }
        if (result.detectedCornersXY == null)
        {
            reason = "MARKER_CORNERS_INVALID reason=NULL_DETECTED_ARRAY";
            return false;
        }
        if (result.projectedCornersXY == null)
        {
            reason = "MARKER_CORNERS_INVALID reason=NULL_PROJECTED_ARRAY";
            return false;
        }
        if (result.markerIds.Length != result.markerCount || result.markerDecisionMargins.Length != result.markerCount)
        {
            reason = $"MARKER_CORNERS_INVALID reason=MARKER_LENGTH_MISMATCH expected={result.markerCount} ids={result.markerIds.Length} margins={result.markerDecisionMargins.Length}";
            return false;
        }
        if (result.detectedCornersXY.Length != expectedCornerFloatCount)
        {
            reason = $"MARKER_CORNERS_INVALID reason=DETECTED_LENGTH_MISMATCH expected={expectedCornerFloatCount} actual={result.detectedCornersXY.Length}";
            return false;
        }
        if (result.projectedCornersXY.Length != expectedCornerFloatCount)
        {
            reason = $"MARKER_CORNERS_INVALID reason=PROJECTED_LENGTH_MISMATCH expected={expectedCornerFloatCount} actual={result.projectedCornersXY.Length}";
            return false;
        }
        if (result.reprojectionResiduals == null || result.reprojectionResiduals.Length != result.correspondenceCount)
        {
            reason = $"MARKER_CORNERS_INVALID reason=RESIDUAL_LENGTH_MISMATCH expected={result.correspondenceCount} actual={result.reprojectionResiduals?.Length ?? 0}";
            return false;
        }

        for (var index = 0; index < expectedCornerFloatCount; index++)
        {
            if (!float.IsFinite(result.detectedCornersXY[index]))
            {
                reason = $"MARKER_CORNERS_INVALID reason=DETECTED_NONFINITE_VALUE index={index}";
                return false;
            }
            if (!float.IsFinite(result.projectedCornersXY[index]))
            {
                reason = $"MARKER_CORNERS_INVALID reason=PROJECTED_NONFINITE_VALUE index={index}";
                return false;
            }
        }
        for (var index = 0; index < result.reprojectionResiduals.Length; index++)
        {
            if (!float.IsFinite(result.reprojectionResiduals[index]))
            {
                reason = $"MARKER_CORNERS_INVALID reason=RESIDUAL_NONFINITE_VALUE index={index}";
                return false;
            }
        }

        var markers = new NativeBoardResult.Marker[result.markerCount];
        for (var markerIndex = 0; markerIndex < result.markerCount; markerIndex++)
        {
            var detectedCorners = new float[4][];
            var projectedCorners = new float[4][];
            for (var cornerIndex = 0; cornerIndex < 4; cornerIndex++)
            {
                var floatOffset = (markerIndex * 4 + cornerIndex) * 2;
                detectedCorners[cornerIndex] = new[] { result.detectedCornersXY[floatOffset], result.detectedCornersXY[floatOffset + 1] };
                projectedCorners[cornerIndex] = new[] { result.projectedCornersXY[floatOffset], result.projectedCornersXY[floatOffset + 1] };
            }
            markers[markerIndex] = new NativeBoardResult.Marker
            {
                id = result.markerIds[markerIndex],
                decisionMargin = result.markerDecisionMargins[markerIndex],
                detectedCorners = detectedCorners,
                projectedCorners = projectedCorners
            };
        }
        result.markers = markers;
        return true;
    }

    private static void LogDeserializedResult(NativeBoardResult result)
    {
        if (result.visionFrameId > 3 && result.visionFrameId % 10 != 0)
            return;

        Debug.Log(
            $"BOARD_RESULT_DESERIALIZED frameId={result.visionFrameId} markerCount={result.markerCount} " +
            $"markerIdsLength={result.markerIds?.Length ?? 0} correspondenceCount={result.correspondenceCount} " +
            $"detectedCornersLength={result.detectedCornersXY?.Length ?? 0} " +
            $"projectedCornersLength={result.projectedCornersXY?.Length ?? 0} " +
            $"residualsLength={result.reprojectionResiduals?.Length ?? 0} representation=FLATTENED_XY_PRIMITIVE_ARRAYS");
    }

    private static void LogVisionTimestamp(NativeBoardResult result, long unityResultReceiveTimestampNs)
    {
        var frameTimestampNs = result.visionFrameTimestampNs != 0L
            ? result.visionFrameTimestampNs
            : result.timestampNs;
        var queueDelayMs = MillisecondsBetween(frameTimestampNs, result.detectorStartTimestampNs);
        var detectorProcessingMs = MillisecondsBetween(result.detectorStartTimestampNs, result.detectorEndTimestampNs);
        var bridgeToUnityDelayMs = MillisecondsBetween(result.detectorEndTimestampNs, unityResultReceiveTimestampNs);
        var totalFrameToUnityMs = MillisecondsBetween(frameTimestampNs, unityResultReceiveTimestampNs);
        Debug.Log(
            $"DJI_VISION_TIMESTAMP frameTimestampNs={frameTimestampNs} detectorStartTimestampNs={result.detectorStartTimestampNs} " +
            $"detectorEndTimestampNs={result.detectorEndTimestampNs} unityResultReceiveTimestampNs={unityResultReceiveTimestampNs} " +
            $"detectorQueueDelayMs={FormatLatency(queueDelayMs)} detectorProcessingMs={FormatLatency(detectorProcessingMs)} " +
            $"bridgeToUnityDelayMs={FormatLatency(bridgeToUnityDelayMs)} totalFrameToUnityMs={FormatLatency(totalFrameToUnityMs)} " +
            "clockDomain=ANDROID_MONOTONIC frameTimestampMeaning=CAMERA_FRAME_LISTENER_CALLBACK_ARRIVAL");
    }

    private static double? MillisecondsBetween(long firstTimestampNs, long secondTimestampNs) =>
        firstTimestampNs > 0L && secondTimestampNs >= firstTimestampNs
            ? (secondTimestampNs - firstTimestampNs) / 1_000_000.0
            : null;

    private static string FormatLatency(double? milliseconds) =>
        milliseconds.HasValue ? $"{milliseconds.Value:F2}ms" : "UNAVAILABLE";

    private bool RequiresVisualReacquisitionHandover() =>
        _visualReacquisitionMode == VisualReacquisitionMode.FreshRelocalization ||
        (_shadowPropagationActive &&
         _hasLatestShadowPrediction &&
         _hasShadowRotationPropagationBaseline);

    private void PromoteHighConfidenceVisualPose(
        NativeBoardResult result,
        VisualPoseMetrics metrics,
        bool shouldLogDiagnosticSnapshot,
        long poseDeliveredToUnityTimestampNs)
    {
        var establishingInitialVisualBaseline = !HasHighConfidenceCameraFromBoard;
        CurrentHighConfidenceCameraFromBoard = metrics.cameraFromBoard;
        _currentHighConfidenceVisualTimestampNs = metrics.timestampNs;
        HasHighConfidenceCameraFromBoard = true;
        _lastHighConfidenceCameraFromBoardRealtime = Time.realtimeSinceStartup;
        _authoritativeVisualTimingSample = new VisualPoseTimingSample(
            result,
            metrics.cameraFromBoard,
            CurrentVisualPoseQuality,
            poseDeliveredToUnityTimestampNs);
        _hasAuthoritativeVisualTimingSample = _authoritativeVisualTimingSample.IsValid;
        TraceOrientationPromotion(result, metrics.cameraFromBoard, poseDeliveredToUnityTimestampNs);
        _staleJumpCluster.Reset();
        _visualTemporalCompensationPredictor.AddAcceptedVisualPose(
            _authoritativeVisualTimingSample.sourceFrameId,
            _authoritativeVisualTimingSample.sourceFrameTimestampNs,
            metrics.cameraFromBoard);
        AuthoritativeVisualPoseAccepted?.Invoke(
            CurrentHighConfidenceCameraFromBoard,
            _currentHighConfidenceVisualTimestampNs);
        if (establishingInitialVisualBaseline)
        {
            Debug.Log(
                "DJI_VISUAL_BASELINE_ESTABLISHED " +
                $"markerCount={metrics.markerCount} " +
                $"rms={metrics.reprojectionRmsPixels:F3} " +
                $"maxCornerResidual={metrics.maxCornerResidualPixels:F3} " +
                $"{GetVisualQualityThresholdSummary(metrics)} " +
                $"confirmationFrames={highConfidenceReacquisitionConfirmationFrames}");
        }

        RecordVisualTelemetryRotationComparison(
            result,
            metrics.cameraFromBoard,
            metrics.markerCount,
            shouldLogDiagnosticSnapshot);
    }

    private void ObserveVisualReacquisitionCandidate(
        NativeBoardResult result,
        VisualPoseMetrics metrics,
        bool shouldLogDiagnosticSnapshot)
    {
        if (!_hasVisualReacquisitionCandidate)
        {
            _hasVisualReacquisitionCandidate = true;
            _visualReacquisitionStartTimestampNs = metrics.timestampNs;
            if (_visualReacquisitionMode == VisualReacquisitionMode.None)
                _visualReacquisitionMode = VisualReacquisitionMode.PropagatedReacquisition;

            if (_visualReacquisitionMode == VisualReacquisitionMode.FreshRelocalization)
            {
                Debug.Log(
                    "DJI_FRESH_VISUAL_RELOCALIZATION_PENDING " +
                    $"markerCount={metrics.markerCount} " +
                    $"rms={metrics.reprojectionRmsPixels:F3} " +
                    $"normalizedRms={metrics.normalizedReprojectionRms:F4}");
            }
        }

        _visualReacquisitionCandidateResult = result;
        _visualReacquisitionCandidateMetrics = metrics;
        _visualReacquisitionCandidateShouldLogDiagnosticSnapshot |= shouldLogDiagnosticSnapshot;
        TryCompleteVisualReacquisitionHandover();
    }

    private void TryCompleteVisualReacquisitionHandover()
    {
        if (_hasVisualReacquisitionCandidate)
            TryCompleteVisualReacquisitionHandoverAtAge(PropagationAgeSeconds);
    }

    private void TryCompleteVisualReacquisitionHandoverAtAge(float propagationAgeSeconds)
    {
        if (!_hasVisualReacquisitionCandidate)
            return;

        if (_visualReacquisitionMode == VisualReacquisitionMode.FreshRelocalization)
        {
            var freshRelocalizationReady = RotationPropagationRenderPolicy.IsFreshVisualRelocalizationReady(
                CurrentVisualPoseQuality == VisualPoseQuality.HighConfidence,
                _visualReacquisitionStartTimestampNs,
                _visualReacquisitionCandidateMetrics.timestampNs,
                reacquisitionStableTimeSeconds);
            if (!freshRelocalizationReady)
                return;

            var freshCandidateResult = _visualReacquisitionCandidateResult;
            var freshCandidateMetrics = _visualReacquisitionCandidateMetrics;
            var freshShouldLogDiagnosticSnapshot = _visualReacquisitionCandidateShouldLogDiagnosticSnapshot;
            var freshDwellMilliseconds = GetVisualReacquisitionAgeMilliseconds();
            ClearVisualReacquisitionCandidate();
            _visualReacquisitionMode = VisualReacquisitionMode.None;
            _acceptedVisualReacquisitionMode = DjiOutageReacquisitionMode.FreshRelocalization;
            PromoteHighConfidenceVisualPose(
                freshCandidateResult,
                freshCandidateMetrics,
                freshShouldLogDiagnosticSnapshot,
                DjiBoardVisionBridge.GetMonotonicTimestampNs());
            Debug.Log(
                "DJI_FRESH_VISUAL_RELOCALIZATION_ACCEPTED " +
                $"dwellMs={freshDwellMilliseconds:F1} {HorizontalRenderModeLogFields()}");
            return;
        }

        // Telemetry propagation intentionally survives the normal two-second
        // window and can accumulate real GNSS error. Its return to vision is a
        // relocalization: keep the visual quality/dwell requirements, but do
        // not compare the board pose to the short-window rotation prediction.
        if (IsTelemetryPropagationSource(CurrentRenderPoseSource) &&
            float.IsFinite(propagationAgeSeconds) &&
            !IsWithinShortPropagationWindow(propagationAgeSeconds))
        {
            // If the outage can no longer continue, let the existing NONE/timeout
            // path expire it instead of granting a long-lived visual handover.
            if (!HasLongLivedTelemetryLocalization())
                return;

            var longLivedReady = RotationPropagationRenderPolicy.IsLongLivedTelemetryReacquisitionReady(
                CurrentVisualPoseQuality == VisualPoseQuality.HighConfidence,
                _visualReacquisitionStartTimestampNs,
                _visualReacquisitionCandidateMetrics.timestampNs,
                reacquisitionStableTimeSeconds);
            if (!_reacquisitionPendingLogged)
            {
                _reacquisitionPendingLogged = true;
                Debug.Log(
                    "DJI_VISUAL_REACQUISITION_PENDING " +
                    $"ageMs={GetVisualReacquisitionAgeMilliseconds():F1} " +
                    "mode=LONG_LIVED_TELEMETRY poseConsistency=SKIPPED");
            }
            if (!longLivedReady)
                return;

            var longLivedCandidateResult = _visualReacquisitionCandidateResult;
            var longLivedCandidateMetrics = _visualReacquisitionCandidateMetrics;
            var longLivedShouldLogDiagnosticSnapshot = _visualReacquisitionCandidateShouldLogDiagnosticSnapshot;
            var longLivedDwellMilliseconds = GetVisualReacquisitionAgeMilliseconds();
            ClearVisualReacquisitionCandidate();
            _acceptedVisualReacquisitionMode = DjiOutageReacquisitionMode.PropagatedReacquisition;
            PromoteHighConfidenceVisualPose(
                longLivedCandidateResult,
                longLivedCandidateMetrics,
                longLivedShouldLogDiagnosticSnapshot,
                DjiBoardVisionBridge.GetMonotonicTimestampNs());
            Debug.Log(
                "DJI_VISUAL_REACQUISITION_ACCEPTED " +
                $"dwellMs={longLivedDwellMilliseconds:F1} " +
                "mode=LONG_LIVED_TELEMETRY poseConsistency=SKIPPED " +
                HorizontalRenderModeLogFields());
            return;
        }

        if (_visualReacquisitionMode != VisualReacquisitionMode.PropagatedReacquisition ||
            !_hasShadowRotationPropagationBaseline ||
            !TryInterpolateTelemetryRotation(
                _visualReacquisitionCandidateMetrics.timestampNs,
                out var telemetryAtVision,
                out _))
        {
            return;
        }

        var propagatedCameraFromBoard = ReferenceFrameTransforms.PredictCameraFromBoardRotationOnly(
            _shadowRotationPropagationBaseline.cameraFromBoardVisual,
            _shadowRotationPropagationBaseline.navigationFromCameraTelemetry,
            telemetryAtVision.navigationFromCameraRotation);
        var ready = RotationPropagationRenderPolicy.IsVisualReacquisitionReady(
            CurrentVisualPoseQuality == VisualPoseQuality.HighConfidence,
            _visualReacquisitionStartTimestampNs,
            _visualReacquisitionCandidateMetrics.timestampNs,
            reacquisitionStableTimeSeconds,
            propagatedCameraFromBoard,
            _visualReacquisitionCandidateMetrics.cameraFromBoard,
            reacquisitionMaximumPositionDifferenceMeters,
            reacquisitionMaximumRotationDifferenceDegrees,
            out var positionDifferenceMeters,
            out var rotationDifferenceDegrees);

        if (!_reacquisitionPendingLogged)
        {
            _reacquisitionPendingLogged = true;
            Debug.Log(
                "DJI_VISUAL_REACQUISITION_PENDING " +
                $"ageMs={GetVisualReacquisitionAgeMilliseconds():F1} " +
                $"positionDifferenceM={positionDifferenceMeters:F3} " +
                $"rotationDifferenceDeg={rotationDifferenceDegrees:F2}");
        }
        if (!ready)
            return;

        var candidateResult = _visualReacquisitionCandidateResult;
        var candidateMetrics = _visualReacquisitionCandidateMetrics;
        var shouldLogDiagnosticSnapshot = _visualReacquisitionCandidateShouldLogDiagnosticSnapshot;
        var dwellMilliseconds = Math.Max(
            0d,
            (candidateMetrics.timestampNs - _visualReacquisitionStartTimestampNs) / 1_000_000.0);
        ClearVisualReacquisitionCandidate();
        _acceptedVisualReacquisitionMode = DjiOutageReacquisitionMode.PropagatedReacquisition;
        PromoteHighConfidenceVisualPose(
            candidateResult,
            candidateMetrics,
            shouldLogDiagnosticSnapshot,
            DjiBoardVisionBridge.GetMonotonicTimestampNs());
        Debug.Log(
            "DJI_VISUAL_REACQUISITION_ACCEPTED " +
            $"dwellMs={dwellMilliseconds:F1} " +
            $"positionDifferenceM={positionDifferenceMeters:F3} " +
            $"rotationDifferenceDeg={rotationDifferenceDegrees:F2} {HorizontalRenderModeLogFields()}");
    }

    private float GetVisualReacquisitionAgeMilliseconds() =>
        _hasVisualReacquisitionCandidate &&
        _visualReacquisitionCandidateMetrics.timestampNs >= _visualReacquisitionStartTimestampNs
            ? (float)((_visualReacquisitionCandidateMetrics.timestampNs - _visualReacquisitionStartTimestampNs) / 1_000_000.0)
            : 0f;

    private void CancelVisualReacquisition(string reason)
    {
        if (!_hasVisualReacquisitionCandidate)
            return;

        if (_visualReacquisitionMode == VisualReacquisitionMode.FreshRelocalization)
        {
            Debug.Log(
                "DJI_FRESH_VISUAL_RELOCALIZATION_CANCELLED " +
                $"reason={reason} ageMs={GetVisualReacquisitionAgeMilliseconds():F1}");
            ClearVisualReacquisitionCandidate();
            return;
        }

        Debug.Log(
            "DJI_VISUAL_REACQUISITION_CANCELLED " +
            $"reason={reason} ageMs={GetVisualReacquisitionAgeMilliseconds():F1}");
        ClearVisualReacquisitionCandidate();
    }

    private void ClearVisualReacquisitionCandidate()
    {
        _hasVisualReacquisitionCandidate = false;
        _visualReacquisitionCandidateResult = null;
        _visualReacquisitionStartTimestampNs = RotationPropagationTiming.NoTimestamp;
        _visualReacquisitionCandidateShouldLogDiagnosticSnapshot = false;
        _reacquisitionPendingLogged = false;
    }

    private void Reject(string reason)
    {
        InterruptStaleJumpCluster(reason);
        _orientationDiagnosticQualityReason = reason;
        State = LocalizationState.Localizing;
        Debug.Log($"DJI_LOCALIZATION_SAMPLE_REJECTED reason={reason}");
    }

    private void RecordVisualTelemetryRotationComparison(
        NativeBoardResult result,
        Pose cameraFromBoard,
        int markerCount,
        bool shouldLogDiagnosticSnapshot)
    {
        if (!compareVisualAndTelemetryRotation &&
            rotationPropagationMode == RotationPropagationMode.Disabled)
            return;

        _telemetryProvider ??= FindFirstObjectByType<DjiCameraPoseProvider>();
        if (_telemetryProvider == null || !_telemetryProvider.HasTelemetryOrientation)
        {
            if (compareVisualAndTelemetryRotation)
                ResetRotationComparison("TELEMETRY_ORIENTATION_INVALID");
            ResetShadowRotationPropagation("TELEMETRY_ORIENTATION_INVALID");
            if (shouldLogDiagnosticSnapshot)
                Debug.Log("DJI_ROTATION_SYNC rejected=TELEMETRY_ORIENTATION_UNAVAILABLE");
            return;
        }

        var visionTimestampNs = GetVisionTimestamp(result);
        if (visionTimestampNs <= 0L)
        {
            if (shouldLogDiagnosticSnapshot)
                Debug.Log("DJI_ROTATION_SYNC rejected=VISION_TIMESTAMP_UNAVAILABLE");
            return;
        }

        if (_pendingRotationDiagnosticSamples.Count >= maximumPendingRotationDiagnosticSamples)
        {
            var dropped = _pendingRotationDiagnosticSamples[0];
            _pendingRotationDiagnosticSamples.RemoveAt(0);
            Debug.Log(
                $"ROTATION_DIAGNOSTIC_SAMPLE_DROPPED reason=PENDING_QUEUE_CAPACITY " +
                $"visionTimestampNs={dropped.visionTimestampNs}");
        }

        _pendingRotationDiagnosticSamples.Add(new PendingRotationDiagnosticSample(
            visionTimestampNs,
            cameraFromBoard,
            markerCount,
            DjiBoardVisionBridge.GetMonotonicTimestampNs(),
            _hasRotationComparisonBaseline ? _rotationComparisonBaselineId : 0,
            shouldLogDiagnosticSnapshot));

        // A bracket can already exist when telemetry polling happened before
        // this vision result. Otherwise OnTelemetryOrientationRecorded retries
        // as soon as the future telemetry endpoint arrives.
        ProcessPendingRotationDiagnosticSamples(DjiBoardVisionBridge.GetMonotonicTimestampNs());
    }

    private void UpdateShadowPropagationState(float visualPoseFreshnessSeconds)
    {
        if (rotationPropagationMode == RotationPropagationMode.Disabled ||
            !_hasShadowRotationPropagationBaseline ||
            _shadowPropagationActive ||
            (CurrentVisualPoseQuality == VisualPoseQuality.HighConfidence &&
             HighConfidenceCameraFromBoardAgeSeconds <= visualPoseFreshnessSeconds))
        {
            return;
        }

        _shadowPropagationActive = true;
        ResetAircraftVelocityVisualValidation("VISUAL_CONTINUITY_INTERRUPTED");
        _hasLatestShadowPrediction = false;
        _hasLatestTranslationShadowPrediction = false;
        _translationShadowStartTimestampNs = DjiBoardVisionBridge.GetMonotonicTimestampNs();
        _rotationPropagationExpirationLogged = false;
        var renderingSource = IsRotationPropagationRenderingEnabled
            ? "TELEMETRY_PROPAGATED"
            : "VISUAL_ONLY";
        Debug.Log(
            "DJI_ROTATION_PROPAGATION_SHADOW state=STARTED " +
            $"visualPoseAgeMs={FormatAgeMilliseconds(HighConfidenceCameraFromBoardAgeSeconds)} " +
            "propagationAgeMs=NOT_STARTED " +
            $"baselineTimestampNs={_shadowRotationPropagationBaseline.timestampNs} " +
            $"capability=ROTATION_ONLY_ZERO_TRANSLATION rendering={renderingSource}");
        if (enableVelocityTranslationShadow)
        {
            Debug.Log(
                "DJI_TRANSLATION_SHADOW_STARTED " +
                $"baselineTimestampNs={_shadowRotationPropagationBaseline.timestampNs} " +
                $"looseMode=SHADOW_LOOSE looseMaxVelocityAgeMs={maximumVelocitySampleAgeForShadowMilliseconds:F0} " +
                $"trustedMode=SHADOW_FRESH trustedMaxVelocityAgeMs={maximumVelocityAgeForTrustedShadowMilliseconds:F0} " +
                "translationModel=ROTATION_PLUS_VELOCITY_TRANSLATION " +
                "cameraLeverArmValidation=PENDING rendering=UNCHANGED");
        }
        if (enableHorizontalGnssTranslationShadow)
        {
            Debug.Log(
                "DJI_TRANSLATION_SHADOW_HORIZONTAL_GNSS_STARTED " +
                $"baselineTimestampNs={_shadowRotationPropagationBaseline.timestampNs} " +
                $"baselinePositionAvailable={_shadowRotationPropagationBaseline.hasHorizontalGnssPosition} " +
                $"maxGnssSampleAgeMs={maximumGnssPositionSampleAgeForShadowMilliseconds:F0} " +
                "translationModel=ROTATION_PLUS_HORIZONTAL_GNSS verticalTranslation=UNAVAILABLE rendering=UNCHANGED");
        }
        if (enableHybridHorizontalPositionShadow)
        {
            Debug.Log(
                "DJI_HYBRID_POSITION_SHADOW_STARTED " +
                $"baselineTimestampNs={_shadowRotationPropagationBaseline.timestampNs} " +
                $"maxGnssSampleAgeMs={maximumGnssPositionSampleAgeForShadowMilliseconds:F0} " +
                $"maxVelocityAgeMs={maximumVelocityAgeForTrustedShadowMilliseconds:F0} " +
                "translationModel=GNSS_PLUS_POST_GNSS_FRESH_VELOCITY verticalSource=UNVALIDATED rendering=UNCHANGED");
        }

        // The prediction formula itself is unchanged. Reusing the newest owned
        // telemetry snapshot only avoids a visual-gap while waiting for the
        // next telemetry callback after a severe visual-quality downgrade.
        if (_telemetryProvider != null &&
            _telemetryProvider.HasTelemetryOrientation &&
            _telemetryProvider.LatestTelemetryArrivalTimestampNs > 0L)
        {
            UpdateShadowPrediction(
                _telemetryProvider.LatestTelemetryArrivalTimestampNs,
                _telemetryProvider.CurrentNavigationCameraPose.rotation);
        }
    }

    private void LogRotationPropagationExpirationIfNeeded(
        float propagationAgeSeconds,
        float visualPoseAgeSeconds)
    {
        if (rotationPropagationMode != RotationPropagationMode.Enabled ||
            !IsRotationPropagationRenderingEnabled ||
            _rotationPropagationExpirationLogged ||
            !float.IsFinite(propagationAgeSeconds) ||
            propagationAgeSeconds <= maxRotationOnlyPropagationSeconds)
        {
            return;
        }

        _rotationPropagationExpirationLogged = true;
        Debug.LogWarning(
            "DJI_ROTATION_PROPAGATION_EXPIRED " +
            $"propagationAgeMs={propagationAgeSeconds * 1000f:F0} " +
            $"baselineAgeMs={BaselineAgeSeconds * 1000f:F0} " +
            $"visualPoseAgeMs={visualPoseAgeSeconds * 1000f:F0} " +
            $"maxDurationMs={maxRotationOnlyPropagationSeconds * 1000f:F0}");
        Debug.LogWarning("DJI_RENDER_POSE_SOURCE=NONE reason=ROTATION_PROPAGATION_EXPIRED");
        // The rendered rotation-only pose expires here, while the diagnostic
        // velocity shadow remains active for longer outage measurements.
    }

    private bool HasRenderedRotationPropagationTimedOut(float propagationAgeSeconds) =>
        rotationPropagationMode == RotationPropagationMode.Enabled &&
        IsRotationPropagationRenderingEnabled &&
        _shadowPropagationActive &&
        _hasShadowRotationPropagationBaseline &&
        float.IsFinite(propagationAgeSeconds) &&
        !IsWithinShortPropagationWindow(propagationAgeSeconds);

    private void ExpireLocalizationSessionAfterPropagationTimeout()
    {
        // A completed propagation interval is not a live spatial hypothesis.
        // The next visual pose must establish a new local reference rather
        // than being measured against the expired board pose.
        ResetShadowRotationPropagation("PROPAGATION_TIMEOUT");
        ResetVisualLocalizationReferencesAfterTimeout();
        _visualReacquisitionMode = VisualReacquisitionMode.FreshRelocalization;
        Debug.Log("DJI_LOCALIZATION_SESSION_EXPIRED reason=PROPAGATION_TIMEOUT");
    }

    private void ResetVisualLocalizationReferencesAfterTimeout()
    {
        _staleJumpCluster.Reset();
        // A post-timeout visual pose begins a new localization interval; do
        // not carry any visual-motion estimate through that discontinuity.
        ResetVisualTemporalCompensationPredictor("FRESH_RELOCALIZATION");
        _lastVisualQualityPose = Pose.identity;
        _hasLastVisualQualityPose = false;
        _lastVisualQualityPoseTimestampNs = RotationPropagationTiming.NoTimestamp;
        _lastVisualQualityTelemetryRotation = Quaternion.identity;
        _hasLastVisualQualityTelemetryRotation = false;
        _consecutiveHighConfidenceVisualPoses = 0;
        _latestTranslationJumpEvaluation = default;
        _lastLoggedTranslationJumpDecision = VisualTranslationJumpDecision.Accept;
        ResetTranslationJumpSuspect();
        ResetBorderlineFourMarkerQualityFailure();
        ClearVisualReacquisitionCandidate();

        HasHighConfidenceCameraFromBoard = false;
        CurrentHighConfidenceCameraFromBoard = Pose.identity;
        _currentHighConfidenceVisualTimestampNs = RotationPropagationTiming.NoTimestamp;
        _lastHighConfidenceCameraFromBoardRealtime = float.NegativeInfinity;
        CurrentVisualPoseQuality = VisualPoseQuality.Unavailable;
    }

    private bool TryResolvePersistentWorldPropagationRenderPose(out Pose cameraFromBoard)
    {
        cameraFromBoard = Pose.identity;
        if (_worldLocalizationShadow == null ||
            _worldLocalizationShadow.Snapshot.phase != WorldLocalizationPhase.Propagating)
        {
            return false;
        }

        return WorldLocalizationPoseResolver.TryGetCameraFromBoard(
            _worldLocalizationShadow.Snapshot,
            out cameraFromBoard);
    }

    /// <summary>
    /// Applies the first experimental rendered-translation policy only after
    /// the normal resolver has already selected TELEMETRY_PROPAGATED. It uses
    /// the exact SHADOW_HORIZONTAL_GNSS transform; velocity and hybrid shadow
    /// predictions are intentionally not candidates here.
    /// </summary>
    private Pose ResolveGuardedHorizontalRenderedPose(
        Pose rotationOnlyCameraFromBoard,
        float propagationAgeSeconds)
    {
        var nowTimestampNs = DjiBoardVisionBridge.GetMonotonicTimestampNs();
        var propagationAgeMilliseconds = float.IsFinite(propagationAgeSeconds)
            ? Mathf.Max(0f, propagationAgeSeconds * 1000f)
            : float.PositiveInfinity;
        var observation = new GuardedHorizontalObservation(
            nowTimestampNs,
            0L,
            propagationAgeMilliseconds,
            hasFreshGnss: false,
            Vector2.zero,
            float.PositiveInfinity);

        if (horizontalTranslationRenderMode != HorizontalTranslationRenderMode.GuardedGnssRendered ||
            !_hasShadowRotationPropagationBaseline ||
            _latestShadowPredictionTimestampNs <= _shadowRotationPropagationBaseline.timestampNs)
        {
            PublishGuardedHorizontalState(
                _guardedHorizontalRenderStateMachine.Update(
                    horizontalTranslationRenderMode,
                    guardedHorizontalRenderConfiguration,
                    observation),
                "ROTATION_ONLY");
            _hasLatestGuardedHorizontalRenderedPose = false;
            return rotationOnlyCameraFromBoard;
        }

        var navigationDisplacement = Vector3.zero;
        var gnssTimestampNs = 0L;
        var gnssReason = "GPS_OR_LOCATION_INVALID";
        var gnssSourceCurrentlyValid = HasCurrentValidHorizontalGnss();
        var hasPrediction = gnssSourceCurrentlyValid && TryBuildHorizontalGnssShadowPrediction(
            _shadowRotationPropagationBaseline,
            _latestShadowPredictionTimestampNs,
            _latestShadowPredictionNavigationFromCamera,
            out _,
            out navigationDisplacement,
            out _,
            out _,
            out gnssTimestampNs,
            out gnssReason);
        if (!gnssSourceCurrentlyValid)
            gnssReason = "GPS_OR_LOCATION_INVALID";
        var gnssAgeMilliseconds = hasPrediction && gnssTimestampNs > 0L && nowTimestampNs >= gnssTimestampNs
            ? (float)((nowTimestampNs - gnssTimestampNs) / 1_000_000.0)
            : float.PositiveInfinity;
        var gnssFreshNow = hasPrediction &&
                           gnssAgeMilliseconds <= guardedHorizontalRenderConfiguration.maxGnssAgeMilliseconds;
        observation = new GuardedHorizontalObservation(
            nowTimestampNs,
            hasPrediction ? gnssTimestampNs : 0L,
            propagationAgeMilliseconds,
            gnssFreshNow,
            new Vector2(navigationDisplacement.x, navigationDisplacement.y),
            gnssAgeMilliseconds);
        var snapshot = _guardedHorizontalRenderStateMachine.Update(
            horizontalTranslationRenderMode,
            guardedHorizontalRenderConfiguration,
            observation);
        PublishGuardedHorizontalState(snapshot, gnssFreshNow ? "FRESH_GNSS" : gnssReason);

        if (!snapshot.active)
        {
            _hasLatestGuardedHorizontalRenderedPose = false;
            return rotationOnlyCameraFromBoard;
        }

        // The transform consumes N/E/D navigation displacement. Down remains
        // deliberately zero, so no vertical source is introduced here.
        var appliedNavigationDisplacement = new Vector3(
            snapshot.appliedCorrectionInNavigation.x,
            snapshot.appliedCorrectionInNavigation.y,
            0f);
        var guardedPose = ReferenceFrameTransforms.PredictCameraFromBoardWithNavigationTranslation(
            _shadowRotationPropagationBaseline.cameraFromBoardVisual,
            _shadowRotationPropagationBaseline.navigationFromCameraTelemetry,
            _latestShadowPredictionNavigationFromCamera,
            appliedNavigationDisplacement);
        _latestGuardedHorizontalRenderedPose = guardedPose;
        _hasLatestGuardedHorizontalRenderedPose = true;
        return guardedPose;
    }

    private bool HasCurrentValidHorizontalGnss()
    {
        var audit = _telemetryProvider?.LatestSnapshot?.positionAudit;
        return audit != null &&
               audit.aircraftLocation3DStatus == "KEY_AVAILABLE_VALID" &&
               audit.gpsIsValidStatus == "KEY_AVAILABLE_VALID" &&
               string.Equals(audit.gpsIsValidRaw, "true", StringComparison.OrdinalIgnoreCase) &&
               audit.gpsSignalLevelStatus == "KEY_AVAILABLE_VALID";
    }

    private void ResetGuardedHorizontalRenderState(string reason, long nowTimestampNs)
    {
        var snapshot = _guardedHorizontalRenderStateMachine.Reset(
            new GuardedHorizontalObservation(
                nowTimestampNs,
                0L,
                float.PositiveInfinity,
                hasFreshGnss: false,
                Vector2.zero,
                float.PositiveInfinity),
            reason);
        PublishGuardedHorizontalState(snapshot, reason);
        _hasLatestGuardedHorizontalRenderedPose = false;
    }

    private string HorizontalRenderModeLogFields() =>
        $"horizontalRenderMode={horizontalTranslationRenderMode} horizontalModeSegmentId={_horizontalModeSegmentId}";

    private void PublishGuardedHorizontalState(GuardedHorizontalStateSnapshot snapshot, string reason)
    {
        _latestGuardedHorizontalRenderSnapshot = new GuardedHorizontalRenderSnapshot(
            horizontalTranslationRenderMode,
            snapshot,
            guardedHorizontalRenderConfiguration);
        _hasLatestGuardedHorizontalRenderSnapshot = true;
        if (_lastLoggedGuardedHorizontalState == snapshot.state)
            return;

        _lastLoggedGuardedHorizontalState = snapshot.state;
        var sampleIntervalMilliseconds =
            snapshot.lastDistinctGnssTimestampNs > snapshot.firstQualifyingSampleTimestampNs
                ? (snapshot.lastDistinctGnssTimestampNs - snapshot.firstQualifyingSampleTimestampNs) / 1_000_000d
                : double.NaN;
        var prefix = snapshot.state switch
        {
            GuardedHorizontalTranslationState.Arming => "DJI_GUARDED_HORIZONTAL_ARMING",
            GuardedHorizontalTranslationState.Active => "DJI_GUARDED_HORIZONTAL_ACTIVE",
            GuardedHorizontalTranslationState.GnssGrace => "DJI_GUARDED_HORIZONTAL_GRACE",
            GuardedHorizontalTranslationState.Deactivating => "DJI_GUARDED_HORIZONTAL_DEACTIVATING",
            _ => "DJI_GUARDED_HORIZONTAL_OFF"
        };
        Debug.Log(
            prefix + " " +
            $"mode={horizontalTranslationRenderMode} propagationAgeMs={FormatPropagationAgeMilliseconds(snapshot.propagationAgeMilliseconds)} " +
            $"gnssAgeMs={FormatPropagationAgeMilliseconds(snapshot.gnssAgeMilliseconds)} " +
            $"correctionNorthM={snapshot.correctionInNavigation.x:F4} correctionEastM={snapshot.correctionInNavigation.y:F4} " +
            $"correctionMagnitudeM={snapshot.correctionInNavigation.magnitude:F4} " +
            $"distinctSamples={snapshot.consistentSampleCount} sampleIntervalMs={FormatPropagationAgeMilliseconds(sampleIntervalMilliseconds)} directionDifferenceDeg={FormatFinite(snapshot.directionDifferenceDegrees)} " +
            $"armingAgeMs={snapshot.armingAgeMilliseconds:F1} armingTimeoutMs={snapshot.armingTimeoutMilliseconds:F1} " +
            $"firstSampleTimestampNs={snapshot.firstQualifyingSampleTimestampNs} lastDistinctGnssTimestampNs={snapshot.lastDistinctGnssTimestampNs} " +
            $"blendAlpha={snapshot.blendAlpha:F3} reason={snapshot.reason} verticalTranslation=UNCHANGED");
    }

    private void UpdateShadowPrediction(long telemetryTimestampNs, Quaternion navigationFromCameraTelemetry)
    {
        if (rotationPropagationMode == RotationPropagationMode.Disabled ||
            !_shadowPropagationActive ||
            !_hasShadowRotationPropagationBaseline)
        {
            return;
        }

        var baseline = _shadowRotationPropagationBaseline;
        var predicted = ReferenceFrameTransforms.PredictCameraFromBoardRotationOnly(
            baseline.cameraFromBoardVisual,
            baseline.navigationFromCameraTelemetry,
            navigationFromCameraTelemetry);
        _latestShadowPredictedCameraFromBoard = predicted;
        _hasLatestShadowPrediction = true;
        _latestShadowPredictionTimestampNs = telemetryTimestampNs;
        _latestShadowPredictionNavigationFromCamera = navigationFromCameraTelemetry;
        // Authoritative world evaluation belongs to the render-time monotonic
        // clock. This callback only retains the timestamped attitude prediction.
        UpdateTranslationShadowPredictionAtLatestComparableTimestamp();

        if (Time.unscaledTime < _nextShadowPropagationLogTime)
            return;

        _nextShadowPropagationLogTime = Time.unscaledTime + shadowPropagationLogIntervalSeconds;
        var deltaRotation = ReferenceFrameTransforms.CameraTFromCamera0Rotation(
            baseline.navigationFromCameraTelemetry,
            navigationFromCameraTelemetry);
        var baselineAgeMilliseconds = Math.Max(0d, (telemetryTimestampNs - baseline.timestampNs) / 1_000_000.0);
        var propagationAgeMilliseconds = _propagationStartTimestampNs > RotationPropagationTiming.NoTimestamp
            ? Math.Max(0d, (telemetryTimestampNs - _propagationStartTimestampNs) / 1_000_000.0)
            : double.NaN;
        var renderingSource = IsRotationPropagationRenderingEnabled
            ? "TELEMETRY_PROPAGATED"
            : "VISUAL_ONLY";
        Debug.Log(
            "DJI_ROTATION_PROPAGATION_SHADOW " +
            $"baselineAgeMs={baselineAgeMilliseconds:F1} propagationAgeMs={FormatPropagationAgeMilliseconds(propagationAgeMilliseconds)} " +
            $"deltaRotationDeg={Quaternion.Angle(Quaternion.identity, deltaRotation):F2} " +
            $"predictedCameraFromBoard.position={predicted.position} " +
            $"predictedCameraFromBoard.rotation={predicted.rotation.eulerAngles} " +
            $"translationModel=ZERO_CAMERA_TRANSLATION rendering={renderingSource}");
    }

    private void UpdateTranslationShadowPredictionAtLatestComparableTimestamp()
    {
        if ((!enableVelocityTranslationShadow && !enableHorizontalGnssTranslationShadow && !enableHybridHorizontalPositionShadow) ||
            !_shadowPropagationActive ||
            !_hasShadowRotationPropagationBaseline ||
            _telemetryProvider == null)
        {
            return;
        }

        // Rotation is interpolated from its own history. Velocity is an
        // event/state signal, so its last state can be held separately.
        var targetTimestampNs = _telemetryProvider.NewestTelemetryOrientationTimestampNs;
        if (targetTimestampNs <= _shadowRotationPropagationBaseline.timestampNs)
        {
            LogTranslationShadowInvalidIfDue("COMMON_TIMESTAMP_NOT_AFTER_BASELINE", targetTimestampNs: targetTimestampNs);
            return;
        }

        if (!TryInterpolateTelemetryRotation(
                targetTimestampNs,
                out var telemetryAtTarget,
                out var rotationReason))
        {
            LogTranslationShadowInvalidIfDue($"ROTATION_{rotationReason}", targetTimestampNs: targetTimestampNs);
            return;
        }

        var hasLoosePrediction = false;
        var prediction = Pose.identity;
        var navigationDisplacement = Vector3.zero;
        var velocityCoverage = default(ReferenceFrameTransforms.VelocityStateHistoryCoverage);
        var reason = "VELOCITY_TRANSLATION_SHADOW_DISABLED";
        var hasFreshPrediction = false;
        var freshNavigationDisplacement = Vector3.zero;
        var freshVelocityCoverage = default(ReferenceFrameTransforms.VelocityStateHistoryCoverage);
        var freshReason = "VELOCITY_TRANSLATION_SHADOW_DISABLED";
        if (enableVelocityTranslationShadow)
        {
            hasLoosePrediction = TryBuildTranslationShadowPrediction(
                _shadowRotationPropagationBaseline,
                targetTimestampNs,
                telemetryAtTarget.navigationFromCameraRotation,
                out prediction,
                out navigationDisplacement,
                out velocityCoverage,
                out reason);
            hasFreshPrediction = TryBuildTranslationShadowPrediction(
                _shadowRotationPropagationBaseline,
                targetTimestampNs,
                telemetryAtTarget.navigationFromCameraRotation,
                maximumVelocityAgeForTrustedShadowMilliseconds,
                out _,
                out freshNavigationDisplacement,
                out freshVelocityCoverage,
                out freshReason);
            LogFreshTranslationShadowIfDue(
                hasFreshPrediction,
                targetTimestampNs,
                hasFreshPrediction ? freshNavigationDisplacement : Vector3.zero,
                freshVelocityCoverage,
                hasFreshPrediction ? "NONE" : freshReason);
        }

        if (enableVelocityTranslationShadow)
        {
            if (hasLoosePrediction)
            {
                _latestTranslationShadowCameraFromBoard = prediction;
                _latestTranslationShadowNavigationDisplacement = navigationDisplacement;
                _latestTranslationShadowTimestampNs = targetTimestampNs;
                _hasLatestTranslationShadowPrediction = true;
                LogTranslationShadowVelocityCoverageIfDue(
                    velocityCoverage,
                    targetTimestampNs,
                    valid: true,
                    reason: "NONE",
                    shadowMode: "SHADOW_LOOSE",
                    maximumSampleAgeMilliseconds: maximumVelocitySampleAgeForShadowMilliseconds);
            }
            else
            {
                LogTranslationShadowInvalidIfDue(reason, velocityCoverage, targetTimestampNs);
            }
        }

        var gnssNavigationDisplacement = Vector3.zero;
        var gnssBaselineAgeMilliseconds = double.PositiveInfinity;
        var gnssTargetAgeMilliseconds = double.PositiveInfinity;
        var gnssReason = "HORIZONTAL_GNSS_TRANSLATION_SHADOW_DISABLED";
        var hasGnssPrediction = enableHorizontalGnssTranslationShadow &&
                                TryBuildHorizontalGnssShadowPrediction(
                                    _shadowRotationPropagationBaseline,
                                    targetTimestampNs,
                                    telemetryAtTarget.navigationFromCameraRotation,
                                    out _,
                                    out gnssNavigationDisplacement,
                                    out gnssBaselineAgeMilliseconds,
                                    out gnssTargetAgeMilliseconds,
                                    out _,
                                    out gnssReason);
        LogHorizontalGnssTranslationShadowIfDue(
            hasGnssPrediction,
            targetTimestampNs,
            hasGnssPrediction ? gnssNavigationDisplacement : Vector3.zero,
            gnssBaselineAgeMilliseconds,
            gnssTargetAgeMilliseconds,
            hasGnssPrediction ? "NONE" : gnssReason);
        if (enableHybridHorizontalPositionShadow)
        {
            var hybridPrediction = BuildHybridPositionShadowPrediction(
                _shadowRotationPropagationBaseline,
                targetTimestampNs,
                telemetryAtTarget.navigationFromCameraRotation);
            LogHybridPositionShadowIfDue(hybridPrediction);
        }

        if (!hasLoosePrediction || Time.unscaledTime < _nextTranslationShadowLogTime)
            return;

        _nextTranslationShadowLogTime = Time.unscaledTime + shadowPropagationLogIntervalSeconds;
        var ageMilliseconds = Math.Max(0d, (targetTimestampNs - _shadowRotationPropagationBaseline.timestampNs) / 1_000_000.0);
        Debug.Log(
            "DJI_TRANSLATION_SHADOW " +
            "mode=SHADOW_LOOSE " +
            $"maxVelocityAgeMs={maximumVelocitySampleAgeForShadowMilliseconds:F0} " +
            $"ageMs={ageMilliseconds:F1} " +
            $"deltaNorthM={navigationDisplacement.x:F4} deltaEastM={navigationDisplacement.y:F4} deltaDownM={navigationDisplacement.z:F4} " +
            $"distanceM={navigationDisplacement.magnitude:F4} valid=true " +
            "cameraLeverArmValidation=PENDING rendering=UNCHANGED");
    }

    private bool TryBuildTranslationShadowPrediction(
        ShadowRotationPropagationBaseline baseline,
        long targetTimestampNs,
        Quaternion navigationFromCameraTarget,
        out Pose prediction,
        out Vector3 navigationDisplacement,
        out ReferenceFrameTransforms.VelocityStateHistoryCoverage velocityCoverage,
        out string reason)
    {
        return TryBuildTranslationShadowPrediction(
            baseline,
            targetTimestampNs,
            navigationFromCameraTarget,
            maximumVelocitySampleAgeForShadowMilliseconds,
            out prediction,
            out navigationDisplacement,
            out velocityCoverage,
            out reason);
    }

    private bool TryBuildTranslationShadowPrediction(
        ShadowRotationPropagationBaseline baseline,
        long targetTimestampNs,
        Quaternion navigationFromCameraTarget,
        float maximumVelocitySampleAgeMilliseconds,
        out Pose prediction,
        out Vector3 navigationDisplacement,
        out ReferenceFrameTransforms.VelocityStateHistoryCoverage velocityCoverage,
        out string reason)
    {
        prediction = Pose.identity;
        navigationDisplacement = Vector3.zero;
        velocityCoverage = default;
        reason = string.Empty;
        if (_telemetryProvider == null)
        {
            reason = "VELOCITY_PROVIDER_UNAVAILABLE";
            return false;
        }

        if (targetTimestampNs < baseline.timestampNs)
        {
            reason = "TARGET_BEFORE_BASELINE";
            return false;
        }

        var maximumSampleAgeNs = (long)(maximumVelocitySampleAgeMilliseconds * 1_000_000d);
        if (!ReferenceFrameTransforms.TryIntegrateNavigationVelocityStateHistory(
                _telemetryProvider.TelemetryVelocityHistory,
                baseline.timestampNs,
                targetTimestampNs,
                maximumSampleAgeNs,
                out navigationDisplacement,
                out velocityCoverage,
                out var integrationReason))
        {
            reason = integrationReason;
            return false;
        }

        prediction = ReferenceFrameTransforms.PredictCameraFromBoardWithNavigationTranslation(
            baseline.cameraFromBoardVisual,
            baseline.navigationFromCameraTelemetry,
            navigationFromCameraTarget,
            navigationDisplacement);
        return true;
    }

    private bool TryBuildHorizontalGnssShadowPrediction(
        ShadowRotationPropagationBaseline baseline,
        long targetTimestampNs,
        Quaternion navigationFromCameraTarget,
        out Pose prediction,
        out Vector3 navigationDisplacement,
        out double baselineAgeMilliseconds,
        out double targetAgeMilliseconds,
        out long targetPositionTimestampNs,
        out string reason)
    {
        prediction = Pose.identity;
        navigationDisplacement = Vector3.zero;
        baselineAgeMilliseconds = double.PositiveInfinity;
        targetAgeMilliseconds = double.PositiveInfinity;
        targetPositionTimestampNs = 0L;
        reason = string.Empty;
        if (_telemetryProvider == null)
        {
            reason = "POSITION_PROVIDER_UNAVAILABLE";
            return false;
        }

        if (!baseline.hasHorizontalGnssPosition)
        {
            reason = "BASELINE_NO_SAMPLE";
            return false;
        }

        if (!DjiAircraftPositionFreshness.IsFreshAtOrBefore(
                baseline.horizontalGnssPosition.timestampNs,
                baseline.timestampNs,
                maximumGnssPositionSampleAgeForShadowMilliseconds,
                out baselineAgeMilliseconds))
        {
            reason = "BASELINE_STALE";
            return false;
        }

        if (!TryGetPositionSampleAtOrBefore(targetTimestampNs, out var targetPosition))
        {
            reason = "NO_SAMPLE";
            return false;
        }

        targetPositionTimestampNs = targetPosition.timestampNs;

        if (!DjiAircraftPositionFreshness.IsFreshAtOrBefore(
                targetPosition.timestampNs,
                targetTimestampNs,
                maximumGnssPositionSampleAgeForShadowMilliseconds,
                out targetAgeMilliseconds))
        {
            reason = "TARGET_STALE";
            return false;
        }

        var horizontal = DjiGeodesy.HorizontalNedDelta(
            baseline.horizontalGnssPosition.latitudeDegrees,
            baseline.horizontalGnssPosition.longitudeDegrees,
            targetPosition.latitudeDegrees,
            targetPosition.longitudeDegrees);
        navigationDisplacement = new Vector3(horizontal.x, horizontal.y, 0f);
        prediction = ReferenceFrameTransforms.PredictCameraFromBoardWithNavigationTranslation(
            baseline.cameraFromBoardVisual,
            baseline.navigationFromCameraTelemetry,
            navigationFromCameraTarget,
            navigationDisplacement);
        return true;
    }

    private HybridPositionShadowPrediction BuildHybridPositionShadowPrediction(
        ShadowRotationPropagationBaseline baseline,
        long targetTimestampNs,
        Quaternion navigationFromCameraTarget)
    {
        var rotationOnly = ReferenceFrameTransforms.PredictCameraFromBoardRotationOnly(
            baseline.cameraFromBoardVisual,
            baseline.navigationFromCameraTelemetry,
            navigationFromCameraTarget);
        var gnssPrediction = Pose.identity;
        var gnssDisplacement = Vector3.zero;
        var gnssBaselineAgeMilliseconds = double.PositiveInfinity;
        var gnssTargetAgeMilliseconds = double.PositiveInfinity;
        var gnssReason = "GNSS_POSITION_UNAVAILABLE";
        // The hybrid diagnostic owns its inputs so disabling either standalone
        // shadow does not silently turn this into rotation-only comparison.
        var hasGnss = TryBuildHorizontalGnssShadowPrediction(
            baseline,
            targetTimestampNs,
            navigationFromCameraTarget,
            out gnssPrediction,
            out gnssDisplacement,
            out gnssBaselineAgeMilliseconds,
            out gnssTargetAgeMilliseconds,
            out _,
            out gnssReason);

        var latestGnssTimestampNs = 0L;
        var velocityStartTimestampNs = 0L;
        var velocityEndTimestampNs = 0L;
        var velocityCoverage = default(ReferenceFrameTransforms.VelocityStateHistoryCoverage);
        var velocityReason = "VELOCITY_DISABLED";
        var hasVelocityAfterGnss = false;
        var velocityIncrement = Vector3.zero;
        if (hasGnss &&
            TryGetFreshPositionSampleAtOrBefore(
                targetTimestampNs,
                out var latestGnss,
                out _,
                out var latestGnssReason))
        {
            latestGnssTimestampNs = latestGnss.timestampNs;
            velocityStartTimestampNs = latestGnssTimestampNs;
            velocityEndTimestampNs = targetTimestampNs;
            if (latestGnssTimestampNs < targetTimestampNs && _telemetryProvider != null)
            {
                hasVelocityAfterGnss = ReferenceFrameTransforms.TryIntegrateNavigationVelocityStateHistory(
                    _telemetryProvider.TelemetryVelocityHistory,
                    latestGnssTimestampNs,
                    targetTimestampNs,
                    (long)(maximumVelocityAgeForTrustedShadowMilliseconds * 1_000_000d),
                    out velocityIncrement,
                    out velocityCoverage,
                    out velocityReason);
            }
            else if (latestGnssTimestampNs == targetTimestampNs)
            {
                velocityReason = "GNSS_AT_TARGET";
            }
            else
            {
                velocityReason = latestGnssReason;
            }
        }

        var freshVelocityDisplacement = Vector3.zero;
        var freshVelocityCoverage = default(ReferenceFrameTransforms.VelocityStateHistoryCoverage);
        var freshVelocityReason = "VELOCITY_UNAVAILABLE";
        var hasFreshVelocityFromBaseline = TryBuildTranslationShadowPrediction(
            baseline,
            targetTimestampNs,
            navigationFromCameraTarget,
            maximumVelocityAgeForTrustedShadowMilliseconds,
            out _,
            out freshVelocityDisplacement,
            out freshVelocityCoverage,
            out freshVelocityReason);
        var mode = ReferenceFrameTransforms.ResolveHybridHorizontalShadowMode(
            hasGnss,
            hasVelocityAfterGnss,
            hasFreshVelocityFromBaseline);
        var navigationDisplacement = mode switch
        {
            ReferenceFrameTransforms.HybridHorizontalShadowMode.GnssPlusVelocity =>
                ReferenceFrameTransforms.CombineHorizontalGnssAndVelocityIncrement(gnssDisplacement, velocityIncrement),
            ReferenceFrameTransforms.HybridHorizontalShadowMode.GnssOnly => gnssDisplacement,
            ReferenceFrameTransforms.HybridHorizontalShadowMode.VelocityOnly =>
                new Vector3(freshVelocityDisplacement.x, freshVelocityDisplacement.y, 0f),
            _ => Vector3.zero
        };
        var selectedCoverage = mode == ReferenceFrameTransforms.HybridHorizontalShadowMode.GnssPlusVelocity
            ? velocityCoverage
            : mode == ReferenceFrameTransforms.HybridHorizontalShadowMode.VelocityOnly
                ? freshVelocityCoverage
                : default;
        var velocityAgeMilliseconds = mode is ReferenceFrameTransforms.HybridHorizontalShadowMode.GnssPlusVelocity or
            ReferenceFrameTransforms.HybridHorizontalShadowMode.VelocityOnly
            ? selectedCoverage.TargetSampleAgeMilliseconds(targetTimestampNs)
            : double.PositiveInfinity;
        var reason = mode switch
        {
            ReferenceFrameTransforms.HybridHorizontalShadowMode.GnssPlusVelocity => "NONE",
            ReferenceFrameTransforms.HybridHorizontalShadowMode.GnssOnly => velocityReason,
            ReferenceFrameTransforms.HybridHorizontalShadowMode.VelocityOnly => gnssReason,
            _ => hasGnss ? velocityReason : $"{gnssReason}|{freshVelocityReason}"
        };
        var prediction = ReferenceFrameTransforms.PredictCameraFromBoardWithNavigationTranslation(
            baseline.cameraFromBoardVisual,
            baseline.navigationFromCameraTelemetry,
            navigationFromCameraTarget,
            navigationDisplacement);
        var snapshot = new HybridPositionShadowSnapshot(
            mode,
            navigationDisplacement,
            targetTimestampNs,
            latestGnssTimestampNs,
            velocityStartTimestampNs,
            velocityEndTimestampNs,
            gnssTargetAgeMilliseconds,
            velocityAgeMilliseconds,
            mode == ReferenceFrameTransforms.HybridHorizontalShadowMode.GnssPlusVelocity ||
            mode == ReferenceFrameTransforms.HybridHorizontalShadowMode.VelocityOnly,
            reason);
        return new HybridPositionShadowPrediction(
            mode == ReferenceFrameTransforms.HybridHorizontalShadowMode.RotationOnlyFallback ? rotationOnly : prediction,
            snapshot,
            mode != ReferenceFrameTransforms.HybridHorizontalShadowMode.RotationOnlyFallback,
            selectedCoverage);
    }

    private void LogHybridPositionShadowIfDue(HybridPositionShadowPrediction prediction)
    {
        _latestHybridPositionShadow = prediction.snapshot;
        _hasLatestHybridPositionShadow = true;
        if (Time.unscaledTime < _nextHybridPositionShadowLogTime)
            return;

        _nextHybridPositionShadowLogTime = Time.unscaledTime + shadowPropagationLogIntervalSeconds;
        var snapshot = prediction.snapshot;
        Debug.Log(
            "DJI_HYBRID_POSITION_SHADOW " +
            $"mode={FormatHybridPositionShadowMode(snapshot.mode)} predictionTimestampNs={snapshot.predictionTimestampNs} " +
            $"latestGnssTimestampNs={snapshot.latestGnssTimestampNs} gnssAgeMs={FormatPropagationAgeMilliseconds(snapshot.gnssAgeMilliseconds)} " +
            $"velocityStartTimestampNs={snapshot.velocityStartTimestampNs} velocityEndTimestampNs={snapshot.velocityEndTimestampNs} " +
            $"velocityAgeMs={FormatPropagationAgeMilliseconds(snapshot.velocityAgeMilliseconds)} " +
            $"velocityCoverageMs={FormatPropagationAgeMilliseconds(snapshot.velocityEndTimestampNs > snapshot.velocityStartTimestampNs ? (snapshot.velocityEndTimestampNs - snapshot.velocityStartTimestampNs) / 1_000_000d : double.NaN)} " +
            $"hybridNorthM={snapshot.navigationDisplacement.x:F4} hybridEastM={snapshot.navigationDisplacement.y:F4} hybridDownM=UNAVAILABLE " +
            $"velocityCoverageValid={snapshot.velocityCoverageValid} reason={snapshot.reason} verticalSource=UNVALIDATED rendering=UNCHANGED");
    }

    private void LogHorizontalGnssTranslationShadowIfDue(
        bool valid,
        long targetTimestampNs,
        Vector3 navigationDisplacement,
        double baselineAgeMilliseconds,
        double targetAgeMilliseconds,
        string reason)
    {
        if (Time.unscaledTime < _nextHorizontalGnssTranslationShadowLogTime)
            return;

        _nextHorizontalGnssTranslationShadowLogTime = Time.unscaledTime + shadowPropagationLogIntervalSeconds;
        var ageMilliseconds = Math.Max(0d, (targetTimestampNs - _shadowRotationPropagationBaseline.timestampNs) / 1_000_000.0);
        Debug.Log(
            "DJI_TRANSLATION_SHADOW_HORIZONTAL_GNSS " +
            "mode=SHADOW_HORIZONTAL_GNSS " +
            $"maxGnssSampleAgeMs={maximumGnssPositionSampleAgeForShadowMilliseconds:F0} ageMs={ageMilliseconds:F1} " +
            $"baselineSampleAgeMs={FormatPropagationAgeMilliseconds(baselineAgeMilliseconds)} targetSampleAgeMs={FormatPropagationAgeMilliseconds(targetAgeMilliseconds)} " +
            $"deltaNorthM={navigationDisplacement.x:F4} deltaEastM={navigationDisplacement.y:F4} deltaDownM=UNAVAILABLE " +
            $"horizontalDistanceM={new Vector2(navigationDisplacement.x, navigationDisplacement.y).magnitude:F4} " +
            $"valid={valid} reason={reason} verticalTranslation=UNAVAILABLE rendering=UNCHANGED");
    }

    private void LogTranslationShadowInvalidIfDue(
        string reason,
        ReferenceFrameTransforms.VelocityStateHistoryCoverage velocityCoverage = default,
        long targetTimestampNs = 0L)
    {
        _hasLatestTranslationShadowPrediction = false;
        if (Time.unscaledTime < _nextTranslationShadowInvalidLogTime)
            return;

        _nextTranslationShadowInvalidLogTime = Time.unscaledTime + shadowPropagationLogIntervalSeconds;
        LogTranslationShadowVelocityCoverage(
            velocityCoverage,
            targetTimestampNs,
            valid: false,
            reason: reason);
        Debug.LogWarning(
            "DJI_TRANSLATION_SHADOW_INVALID " +
            $"reason={reason} baselineTimestampNs={_shadowRotationPropagationBaseline.timestampNs} " +
            $"orientationNewestTimestampNs={_telemetryProvider?.NewestTelemetryOrientationTimestampNs ?? 0L} " +
            $"velocityNewestTimestampNs={_telemetryProvider?.NewestVelocityHistoryTimestampNs ?? 0L}");
    }

    private void LogTranslationShadowVelocityCoverageIfDue(
        ReferenceFrameTransforms.VelocityStateHistoryCoverage velocityCoverage,
        long targetTimestampNs,
        bool valid,
        string reason,
        string shadowMode = "SHADOW_LOOSE",
        float maximumSampleAgeMilliseconds = 0f)
    {
        if (Time.unscaledTime < _nextTranslationShadowLogTime)
            return;

        LogTranslationShadowVelocityCoverage(
            velocityCoverage,
            targetTimestampNs,
            valid,
            reason,
            shadowMode,
            maximumSampleAgeMilliseconds > 0f
                ? maximumSampleAgeMilliseconds
                : maximumVelocitySampleAgeForShadowMilliseconds);
    }

    private void LogTranslationShadowVelocityCoverage(
        ReferenceFrameTransforms.VelocityStateHistoryCoverage velocityCoverage,
        long targetTimestampNs,
        bool valid,
        string reason,
        string shadowMode = "SHADOW_LOOSE",
        float maximumSampleAgeMilliseconds = 0f)
    {
        var baselineTimestampNs = _shadowRotationPropagationBaseline.timestampNs;
        if (targetTimestampNs <= 0L)
            targetTimestampNs = _telemetryProvider?.NewestTelemetryOrientationTimestampNs ?? 0L;
        Debug.Log(
            "DJI_TRANSLATION_SHADOW_VELOCITY_COVERAGE " +
            $"baselineSampleAgeMs={velocityCoverage.BaselineSampleAgeMilliseconds(baselineTimestampNs):F1} " +
            $"targetSampleAgeMs={velocityCoverage.TargetSampleAgeMilliseconds(targetTimestampNs):F1} " +
            $"callbackCountInsideInterval={velocityCoverage.callbackCountInsideInterval} " +
            $"shadowMode={shadowMode} maxVelocityAgeMs={(maximumSampleAgeMilliseconds > 0f ? maximumSampleAgeMilliseconds : maximumVelocitySampleAgeForShadowMilliseconds):F0} " +
            "integrationMode=ZERO_ORDER_HOLD " +
            $"valid={valid} reason={reason}");
    }

    private void LogFreshTranslationShadowIfDue(
        bool valid,
        long targetTimestampNs,
        Vector3 navigationDisplacement,
        ReferenceFrameTransforms.VelocityStateHistoryCoverage velocityCoverage,
        string reason)
    {
        if (Time.unscaledTime < _nextFreshTranslationShadowLogTime)
            return;

        _nextFreshTranslationShadowLogTime = Time.unscaledTime + shadowPropagationLogIntervalSeconds;
        LogTranslationShadowVelocityCoverage(
            velocityCoverage,
            targetTimestampNs,
            valid,
            reason,
            "SHADOW_FRESH",
            maximumVelocityAgeForTrustedShadowMilliseconds);
        if (!valid)
        {
            Debug.LogWarning(
                "DJI_TRANSLATION_SHADOW_FRESH_INVALID " +
                $"reason={reason} trustedMaxVelocityAgeMs={maximumVelocityAgeForTrustedShadowMilliseconds:F0} " +
                "rendering=UNCHANGED");
            return;
        }

        var ageMilliseconds = Math.Max(0d, (targetTimestampNs - _shadowRotationPropagationBaseline.timestampNs) / 1_000_000.0);
        Debug.Log(
            "DJI_TRANSLATION_SHADOW_FRESH " +
            $"trustedMaxVelocityAgeMs={maximumVelocityAgeForTrustedShadowMilliseconds:F0} ageMs={ageMilliseconds:F1} " +
            $"deltaNorthM={navigationDisplacement.x:F4} deltaEastM={navigationDisplacement.y:F4} deltaDownM={navigationDisplacement.z:F4} " +
            $"distanceM={navigationDisplacement.magnitude:F4} valid=true rendering=UNCHANGED");
    }

    private void QueueReacquisitionDiagnosticsAndStopRendering()
    {
        if (_hasShadowRotationPropagationBaseline &&
            _translationShadowStartTimestampNs > RotationPropagationTiming.NoTimestamp)
        {
            _pendingReacquisitionBaseline = _shadowRotationPropagationBaseline;
            _pendingReacquisitionStartTimestampNs = _translationShadowStartTimestampNs;
            _hasPendingReacquisition = true;
        }

        StopRenderedPropagation();
    }

    private void BeginOutageVisualGroundTruthCapture(long outageStartTimestampNs)
    {
        _hasPendingOutagePredictionCapture = false;
        if (!_outageVisualGroundTruth.TryBegin(
                _localizationSessionId,
                _lastAuthoritativeVisualPose,
                outageStartTimestampNs,
                _horizontalModeSegmentId,
                horizontalTranslationRenderMode,
                out var outage))
        {
            return;
        }

        Debug.Log(
            "DJI_OUTAGE_STARTED " +
            $"outageId={outage.outageId} localizationSessionId={outage.localizationSessionId} " +
            $"preVisualTimestampNs={outage.preVisualPose.timestampNs} " +
            $"horizontalModeSegmentId={outage.horizontalModeSegmentId} " +
            $"horizontalRenderMode={outage.horizontalRenderMode}");
    }

    private void RecordAuthoritativeVisualPose()
    {
        if (_currentHighConfidenceVisualTimestampNs <= RotationPropagationTiming.NoTimestamp)
            return;

        TraceOrientationReference();
        _lastAuthoritativeVisualPose = new DjiOutageVisualGroundTruthTracker.TrustedVisualPose(
            CurrentHighConfidenceCameraFromBoard,
            _currentHighConfidenceVisualTimestampNs);
    }

    private void CaptureOutagePredictionsAtAcceptedVisualPose(
        long postVisualTimestampNs,
        double propagationDurationMilliseconds,
        Pose rotationOnlyPrediction,
        bool hasHorizontalGnssShadow,
        Pose horizontalGnssShadowPrediction,
        HybridPositionShadowPrediction hybridPositionShadow)
    {
        if (!_outageVisualGroundTruth.HasActiveOutage || _hasPendingOutagePredictionCapture)
            return;

        var hasGuardedRenderedPrediction = _hasLatestGuardedHorizontalRenderedPose &&
                                           horizontalTranslationRenderMode == HorizontalTranslationRenderMode.GuardedGnssRendered;
        var guardedSnapshot = _latestGuardedHorizontalRenderSnapshot;
        _pendingOutagePredictionCapture = new OutagePredictionCapture(
            postVisualTimestampNs,
            propagationDurationMilliseconds,
            new DjiOutagePredictionPose(true, rotationOnlyPrediction),
            new DjiOutagePredictionPose(hasGuardedRenderedPrediction, _latestGuardedHorizontalRenderedPose),
            new DjiOutagePredictionPose(hasHorizontalGnssShadow, horizontalGnssShadowPrediction),
            new DjiOutagePredictionPose(
                enableHybridHorizontalPositionShadow && hybridPositionShadow.hasTranslationCorrection,
                hybridPositionShadow.cameraFromBoard),
            guardedSnapshot);
        _hasPendingOutagePredictionCapture = true;
    }

    /// <summary>
    /// Captures the last pose actually rendered during a propagated outage.
    /// This runs before visual authority and reset paths mutate live state.
    /// A timeout/fresh relocalization has no propagated render pose and remains
    /// intentionally invalid rather than fabricating a prediction.
    /// </summary>
    private void CapturePreVisualHandoverSnapshot(
        DjiRenderPoseSource renderSourceBeforeHandover,
        long postVisualTimestampNs)
    {
        if (!_outageVisualGroundTruth.HasActiveOutage ||
            !IsTelemetryPropagationSource(renderSourceBeforeHandover) ||
            !_hasLatestShadowPrediction ||
            !_hasShadowRotationPropagationBaseline)
        {
            return;
        }

        var propagationDurationMilliseconds = _propagationStartTimestampNs > RotationPropagationTiming.NoTimestamp &&
                                              _latestShadowPredictionTimestampNs >= _propagationStartTimestampNs
            ? (_latestShadowPredictionTimestampNs - _propagationStartTimestampNs) / 1_000_000.0
            : double.NaN;
        var guardedSnapshot = _latestGuardedHorizontalRenderSnapshot;
        var guardedStateRendersPose = guardedSnapshot.state is GuardedHorizontalTranslationState.Active or
            GuardedHorizontalTranslationState.GnssGrace or
            GuardedHorizontalTranslationState.Deactivating;
        var hasGuardedRenderedPose = horizontalTranslationRenderMode == HorizontalTranslationRenderMode.GuardedGnssRendered &&
                                     guardedStateRendersPose &&
                                     _hasLatestGuardedHorizontalRenderedPose &&
                                     guardedSnapshot.appliedCorrectionInNavigation.sqrMagnitude > 0f;

        var gnssShadowPrediction = Pose.identity;
        var hasGnssShadow = enableHorizontalGnssTranslationShadow &&
                            TryBuildHorizontalGnssShadowPrediction(
                                _shadowRotationPropagationBaseline,
                                _latestShadowPredictionTimestampNs,
                                _latestShadowPredictionNavigationFromCamera,
                                out gnssShadowPrediction,
                                out _,
                                out _,
                                out _,
                                out _,
                                out _);
        var hybridShadow = enableHybridHorizontalPositionShadow
            ? BuildHybridPositionShadowPrediction(
                _shadowRotationPropagationBaseline,
                _latestShadowPredictionTimestampNs,
                _latestShadowPredictionNavigationFromCamera)
            : default;

        _pendingOutagePredictionCapture = new OutagePredictionCapture(
            postVisualTimestampNs,
            propagationDurationMilliseconds,
            new DjiOutagePredictionPose(true, _latestShadowPredictedCameraFromBoard),
            new DjiOutagePredictionPose(hasGuardedRenderedPose, _latestGuardedHorizontalRenderedPose),
            new DjiOutagePredictionPose(hasGnssShadow, gnssShadowPrediction),
            new DjiOutagePredictionPose(
                enableHybridHorizontalPositionShadow && hybridShadow.hasTranslationCorrection,
                hybridShadow.cameraFromBoard),
            guardedSnapshot);
        _hasPendingOutagePredictionCapture = true;
    }

    private void CompleteOutageVisualGroundTruthCapture(DjiOutageReacquisitionMode reacquisitionMode)
    {
        if (!_outageVisualGroundTruth.TryComplete(out var outage))
            return;

        var postVisualPose = new DjiOutageVisualGroundTruthTracker.TrustedVisualPose(
            CurrentHighConfidenceCameraFromBoard,
            _currentHighConfidenceVisualTimestampNs);
        var canMeasureVisualDisplacement = outage.preVisualPose.IsValid && postVisualPose.IsValid;
        var visualDeltaInBoard = canMeasureVisualDisplacement
            ? DjiOutageVisualGroundTruthTracker.CameraDisplacementInBoard(outage.preVisualPose, postVisualPose)
            : Vector3.zero;
        var visualRotationDifferenceDegrees = Quaternion.Angle(
            outage.preVisualPose.cameraFromBoard.rotation,
            postVisualPose.cameraFromBoard.rotation);
        var hasMatchedPredictionCapture = _hasPendingOutagePredictionCapture &&
                                          _pendingOutagePredictionCapture.postVisualTimestampNs == postVisualPose.timestampNs;
        var predictionCapture = hasMatchedPredictionCapture
            ? _pendingOutagePredictionCapture
            : default;
        var guarded = hasMatchedPredictionCapture
            ? predictionCapture.guardedSnapshot
            : _latestGuardedHorizontalRenderSnapshot;
        var outageDurationMilliseconds = outage.outageStartTimestampNs > 0L &&
                                        postVisualPose.timestampNs >= outage.outageStartTimestampNs
            ? (postVisualPose.timestampNs - outage.outageStartTimestampNs) / 1_000_000.0
            : double.NaN;
        var summary = new DjiOutageReacquisitionSummary(
            outage,
            reacquisitionMode,
            postVisualPose,
            outageDurationMilliseconds,
            hasMatchedPredictionCapture ? predictionCapture.propagationDurationMilliseconds : double.NaN,
            canMeasureVisualDisplacement,
            visualDeltaInBoard,
            visualRotationDifferenceDegrees,
            hasMatchedPredictionCapture ? predictionCapture.rotationOnly : default,
            hasMatchedPredictionCapture ? predictionCapture.guardedRendered : default,
            hasMatchedPredictionCapture ? predictionCapture.gnssShadow : default,
            hasMatchedPredictionCapture ? predictionCapture.hybridShadow : default,
            guarded.state,
            guarded.appliedCorrectionInNavigation.magnitude,
            guarded.correctionInNavigation.magnitude,
            guarded.blendAlpha);
        _telemetryValidationLogger?.WriteOutageReacquisitionSummary(summary);
        _hasPendingOutagePredictionCapture = false;

        // A fresh relocalization closes the previous physical outage before
        // starting a new independent localization session for later outages.
        if (reacquisitionMode == DjiOutageReacquisitionMode.FreshRelocalization)
            _localizationSessionId++;
    }

    private bool TryTakeReacquisitionContext(
        out ShadowRotationPropagationBaseline baseline,
        out long propagationStartTimestampNs)
    {
        if (_hasPendingReacquisition)
        {
            baseline = _pendingReacquisitionBaseline;
            propagationStartTimestampNs = _pendingReacquisitionStartTimestampNs;
            _hasPendingReacquisition = false;
            _pendingReacquisitionStartTimestampNs = RotationPropagationTiming.NoTimestamp;
            return true;
        }

        if (_shadowPropagationActive &&
            _hasShadowRotationPropagationBaseline &&
            _translationShadowStartTimestampNs > RotationPropagationTiming.NoTimestamp)
        {
            baseline = _shadowRotationPropagationBaseline;
            propagationStartTimestampNs = _translationShadowStartTimestampNs;
            return true;
        }

        baseline = default;
        propagationStartTimestampNs = RotationPropagationTiming.NoTimestamp;
        return false;
    }

    private void StopRenderedPropagation()
    {
        _worldLocalizationShadow?.StopRotationPropagation();
        ResetGuardedHorizontalRenderState("PROPAGATION_STOPPED", DjiBoardVisionBridge.GetMonotonicTimestampNs());
        _shadowPropagationActive = false;
        _hasLatestShadowPrediction = false;
        _latestShadowPredictionTimestampNs = RotationPropagationTiming.NoTimestamp;
        _latestShadowPredictionNavigationFromCamera = Quaternion.identity;
        _hasLatestTranslationShadowPrediction = false;
        _hasLatestHybridPositionShadow = false;
        _translationShadowStartTimestampNs = RotationPropagationTiming.Reset();
        _propagationStartTimestampNs = RotationPropagationTiming.Reset();
        _rotationPropagationExpirationLogged = false;
    }

    private void LogRotationPropagationState(
        DjiRenderPoseSource source,
        float visualPoseAgeSeconds,
        float propagationAgeSeconds,
        bool force)
    {
        if (!force && Time.unscaledTime < _nextRotationPropagationStateLogTime)
            return;

        _nextRotationPropagationStateLogTime = Time.unscaledTime + shadowPropagationLogIntervalSeconds;
        Debug.Log(
            "DJI_ROTATION_PROPAGATION_STATE " +
            $"baselineAgeMs={FormatAgeMilliseconds(BaselineAgeSeconds)} " +
            $"propagationAgeMs={FormatAgeMilliseconds(propagationAgeSeconds)} " +
            $"visualPoseAgeMs={FormatAgeMilliseconds(visualPoseAgeSeconds)} " +
            $"source={FormatRenderPoseSource(source)} " +
            $"telemetryPropagationPhase={GetTelemetryPropagationDiagnosticPhase(source, propagationAgeSeconds) ?? "NONE"}");
    }

    private static string FormatPropagationAgeMilliseconds(double ageMilliseconds) =>
        double.IsFinite(ageMilliseconds) ? ageMilliseconds.ToString("F1") : "NOT_STARTED";

    private static string FormatAgeMilliseconds(float ageSeconds) =>
        float.IsFinite(ageSeconds) ? (ageSeconds * 1000f).ToString("F0") : "UNAVAILABLE";

    private bool HasLongLivedTelemetryLocalization() =>
        _shadowPropagationActive &&
        _hasShadowRotationPropagationBaseline &&
        _hasLatestShadowPrediction &&
        _telemetryProvider != null &&
        _telemetryProvider.HasTelemetryOrientation;

    private static bool IsTelemetryPropagationSource(DjiRenderPoseSource source) =>
        source == DjiRenderPoseSource.TelemetryPropagated;

    private bool IsWithinShortPropagationWindow(float ageSeconds) =>
        RotationPropagationRenderPolicy.IsWithinShortPropagationWindow(ageSeconds, maxRotationOnlyPropagationSeconds);

    private static string FormatRenderPoseSource(DjiRenderPoseSource source) => source switch
    {
        DjiRenderPoseSource.Visual => "VISUAL",
        DjiRenderPoseSource.TelemetryPropagated => "TELEMETRY_PROPAGATED",
        _ => "NONE"
    };

    private static string FormatHybridPositionShadowMode(
        ReferenceFrameTransforms.HybridHorizontalShadowMode mode) => mode switch
    {
        ReferenceFrameTransforms.HybridHorizontalShadowMode.GnssPlusVelocity => "HYBRID_GNSS_PLUS_VELOCITY",
        ReferenceFrameTransforms.HybridHorizontalShadowMode.GnssOnly => "HYBRID_GNSS_ONLY",
        ReferenceFrameTransforms.HybridHorizontalShadowMode.VelocityOnly => "HYBRID_VELOCITY_ONLY",
        _ => "HYBRID_ROTATION_ONLY_FALLBACK"
    };

    private void CaptureShadowBaselineOrCompareReacquisition(
        PendingRotationDiagnosticSample pending,
        InterpolatedTelemetryRotation telemetryAtVision)
    {
        if (rotationPropagationMode == RotationPropagationMode.Disabled)
            return;

        // A paired telemetry sample can arrive while a visual candidate is
        // still pending. It is diagnostic-only until render authority actually
        // changes to VISUAL; consuming it here used to stop propagation and
        // reset its lifetime before that handover was accepted.
        if (RotationPropagationRenderPolicy.ShouldPreservePropagationForProvisionalVisual(CurrentRenderPoseSource))
            return;

        if (TryTakeReacquisitionContext(out var baseline, out var propagationStartTimestampNs))
        {
            var rotationOnlyPrediction = ReferenceFrameTransforms.PredictCameraFromBoardRotationOnly(
                baseline.cameraFromBoardVisual,
                baseline.navigationFromCameraTelemetry,
                telemetryAtVision.navigationFromCameraRotation);
            var velocityShadowPrediction = Pose.identity;
            var navigationDisplacement = Vector3.zero;
            var velocityShadowReason = "VELOCITY_TRANSLATION_SHADOW_DISABLED";
            var hasVelocityShadow = enableVelocityTranslationShadow &&
                                    TryBuildTranslationShadowPrediction(
                                        baseline,
                                        pending.visionTimestampNs,
                                        telemetryAtVision.navigationFromCameraRotation,
                                        out velocityShadowPrediction,
                                        out navigationDisplacement,
                                        out _,
                                        out velocityShadowReason);
            var freshVelocityShadowPrediction = Pose.identity;
            var freshNavigationDisplacement = Vector3.zero;
            var freshVelocityShadowReason = "VELOCITY_TRANSLATION_SHADOW_DISABLED";
            var hasFreshVelocityShadow = enableVelocityTranslationShadow &&
                                         TryBuildTranslationShadowPrediction(
                                             baseline,
                                             pending.visionTimestampNs,
                                             telemetryAtVision.navigationFromCameraRotation,
                                             maximumVelocityAgeForTrustedShadowMilliseconds,
                                             out freshVelocityShadowPrediction,
                                             out freshNavigationDisplacement,
                                             out _,
                                             out freshVelocityShadowReason);
            var horizontalGnssShadowPrediction = Pose.identity;
            var horizontalGnssNavigationDisplacement = Vector3.zero;
            var horizontalGnssBaselineAgeMilliseconds = double.PositiveInfinity;
            var horizontalGnssTargetAgeMilliseconds = double.PositiveInfinity;
            var horizontalGnssShadowReason = "HORIZONTAL_GNSS_TRANSLATION_SHADOW_DISABLED";
            var hasHorizontalGnssShadow = enableHorizontalGnssTranslationShadow &&
                                          TryBuildHorizontalGnssShadowPrediction(
                                              baseline,
                                              pending.visionTimestampNs,
                                              telemetryAtVision.navigationFromCameraRotation,
                                              out horizontalGnssShadowPrediction,
                                              out horizontalGnssNavigationDisplacement,
                                              out horizontalGnssBaselineAgeMilliseconds,
                                              out horizontalGnssTargetAgeMilliseconds,
                                              out _,
                                              out horizontalGnssShadowReason);
            var hybridPositionShadow = enableHybridHorizontalPositionShadow
                ? BuildHybridPositionShadowPrediction(
                    baseline,
                    pending.visionTimestampNs,
                    telemetryAtVision.navigationFromCameraRotation)
                : default;
            var propagationDurationMilliseconds = Math.Max(
                0d,
                (pending.visionTimestampNs - propagationStartTimestampNs) / 1_000_000.0);
            CaptureOutagePredictionsAtAcceptedVisualPose(
                pending.visionTimestampNs,
                propagationDurationMilliseconds,
                rotationOnlyPrediction,
                hasHorizontalGnssShadow,
                hasHorizontalGnssShadow ? horizontalGnssShadowPrediction : Pose.identity,
                hybridPositionShadow);
            LogShadowReacquisition(
                propagationDurationMilliseconds,
                rotationOnlyPrediction,
                hasVelocityShadow ? velocityShadowPrediction : Pose.identity,
                hasVelocityShadow ? navigationDisplacement : Vector3.zero,
                hasVelocityShadow ? string.Empty : velocityShadowReason,
                hasFreshVelocityShadow ? freshVelocityShadowPrediction : Pose.identity,
                hasFreshVelocityShadow ? freshNavigationDisplacement : Vector3.zero,
                hasFreshVelocityShadow ? string.Empty : freshVelocityShadowReason,
                hasHorizontalGnssShadow ? horizontalGnssShadowPrediction : Pose.identity,
                hasHorizontalGnssShadow ? horizontalGnssNavigationDisplacement : Vector3.zero,
                horizontalGnssBaselineAgeMilliseconds,
                horizontalGnssTargetAgeMilliseconds,
                hasHorizontalGnssShadow ? string.Empty : horizontalGnssShadowReason,
                hybridPositionShadow,
                pending.cameraFromBoard,
                telemetryAtVision.navigationFromCameraRotation);
            StopRenderedPropagation();
        }
        else if (_shadowPropagationActive)
        {
            // ShadowDiagnostic never starts a render timer, but a new visual
            // baseline still ends that diagnostic-only board-loss interval.
            StopRenderedPropagation();
        }
        else if (_hasShadowRotationPropagationBaseline)
        {
            LogVelocityScaleDiagnostic(
                _shadowRotationPropagationBaseline,
                pending.visionTimestampNs,
                telemetryAtVision.navigationFromCameraRotation,
                pending.cameraFromBoard);
        }

        // Refresh the baseline only from a timestamp-matched, accepted visual
        // pose. It remains unchanged while the board is absent.
        var horizontalGnssBaseline = default(TelemetryAircraftPositionSample);
        var freshHorizontalGnssBaselineAgeMilliseconds = double.PositiveInfinity;
        var hasFreshHorizontalGnssBaseline = enableHorizontalGnssTranslationShadow &&
                                             TryGetFreshPositionSampleAtOrBefore(
                                                 pending.visionTimestampNs,
                                                 out horizontalGnssBaseline,
                                                 out freshHorizontalGnssBaselineAgeMilliseconds,
                                                 out _);
        _shadowRotationPropagationBaseline = new ShadowRotationPropagationBaseline(
            pending.visionTimestampNs,
            pending.cameraFromBoard,
            telemetryAtVision.navigationFromCameraRotation,
            hasFreshHorizontalGnssBaseline,
            horizontalGnssBaseline);
        _hasShadowRotationPropagationBaseline = true;
        if (pending.shouldLogDiagnosticSnapshot)
        {
            Debug.Log(
                "DJI_ROTATION_PROPAGATION_BASELINE_CAPTURED " +
                $"timestampNs={pending.visionTimestampNs} " +
                $"cameraFromBoard.position={pending.cameraFromBoard.position} " +
                $"cameraFromBoard.rotation={pending.cameraFromBoard.rotation.eulerAngles} " +
                $"horizontalGnssBaselineAvailable={hasFreshHorizontalGnssBaseline} " +
                $"horizontalGnssBaselineAgeMs={FormatPropagationAgeMilliseconds(freshHorizontalGnssBaselineAgeMilliseconds)}");
        }
    }

    private void LogShadowReacquisition(
        double propagationDurationMilliseconds,
        Pose rotationOnlyPrediction,
        Pose velocityShadowPrediction,
        Vector3 navigationDisplacement,
        string velocityShadowInvalidReason,
        Pose freshVelocityShadowPrediction,
        Vector3 freshNavigationDisplacement,
        string freshVelocityShadowInvalidReason,
        Pose horizontalGnssShadowPrediction,
        Vector3 horizontalGnssNavigationDisplacement,
        double horizontalGnssBaselineAgeMilliseconds,
        double horizontalGnssTargetAgeMilliseconds,
        string horizontalGnssShadowInvalidReason,
        HybridPositionShadowPrediction hybridPositionShadow,
        Pose visualCameraFromBoard,
        Quaternion navigationFromCameraAtReacquisition)
    {
        var rotationErrorDegrees = Quaternion.Angle(
            rotationOnlyPrediction.rotation,
            visualCameraFromBoard.rotation);
        var rotationOnlyPositionErrorMeters = Vector3.Distance(
            rotationOnlyPrediction.position,
            visualCameraFromBoard.position);
        var hasVelocityShadow = string.IsNullOrEmpty(velocityShadowInvalidReason);
        var velocityShadowPositionErrorMeters = hasVelocityShadow
            ? Vector3.Distance(velocityShadowPrediction.position, visualCameraFromBoard.position)
            : float.NaN;
        var improvementMeters = hasVelocityShadow
            ? rotationOnlyPositionErrorMeters - velocityShadowPositionErrorMeters
            : float.NaN;
        var improvementPercent = hasVelocityShadow && rotationOnlyPositionErrorMeters > 0.0001f
            ? improvementMeters / rotationOnlyPositionErrorMeters * 100f
            : float.NaN;
        var visualFromPredicted = Quaternion.Normalize(
            visualCameraFromBoard.rotation * Quaternion.Inverse(rotationOnlyPrediction.rotation));
        var errorEuler = visualFromPredicted.eulerAngles;
        var pitchErrorDegrees = Mathf.DeltaAngle(0f, errorEuler.x);
        var yawErrorDegrees = Mathf.DeltaAngle(0f, errorEuler.y);
        var rollErrorDegrees = Mathf.DeltaAngle(0f, errorEuler.z);
        var bucket = GetReacquisitionDurationBucket(propagationDurationMilliseconds);
        if (!_shadowReacquisitionStatistics.TryGetValue(bucket, out var statistics))
        {
            statistics = new RotationErrorStatistics();
            _shadowReacquisitionStatistics.Add(bucket, statistics);
        }
        statistics.Add(rotationErrorDegrees);

        Debug.Log(
            "ROTATION_PROPAGATION_REACQUISITION " +
            $"propagationDurationMs={propagationDurationMilliseconds:F1} durationBucket={FormatReacquisitionDurationBucket(bucket)} " +
            $"rotationErrorDeg={rotationErrorDegrees:F2} positionErrorM={rotationOnlyPositionErrorMeters:F3} " +
            $"yawErrorDeg={yawErrorDegrees:F2} pitchErrorDeg={pitchErrorDegrees:F2} rollErrorDeg={rollErrorDegrees:F2} " +
            "positionErrorInterpretation=EXPECTED_TO_INCLUDE_UNMODELED_CAMERA_TRANSLATION");
        Debug.Log(
            "DJI_TRANSLATION_SHADOW_REACQUISITION " +
            "mode=SHADOW_LOOSE " +
            $"maxVelocityAgeMs={maximumVelocitySampleAgeForShadowMilliseconds:F0} " +
            $"outageMs={propagationDurationMilliseconds:F1} " +
            $"integratedNorthM={navigationDisplacement.x:F4} integratedEastM={navigationDisplacement.y:F4} integratedDownM={navigationDisplacement.z:F4} " +
            $"integratedDistanceM={navigationDisplacement.magnitude:F4} " +
            $"rotationOnlyPositionErrorM={rotationOnlyPositionErrorMeters:F4} " +
            $"velocityPositionErrorM={velocityShadowPositionErrorMeters:F4} " +
            $"rotationOnlyRotationErrorDeg={rotationErrorDegrees:F2} velocityRotationErrorDeg={rotationErrorDegrees:F2} " +
            $"improvementM={improvementMeters:F4} improvementPercent={improvementPercent:F1} " +
            $"valid={hasVelocityShadow} reason={(hasVelocityShadow ? "NONE" : velocityShadowInvalidReason)} " +
            "cameraLeverArmValidation=PENDING rendering=UNCHANGED");
        var hasFreshVelocityShadow = string.IsNullOrEmpty(freshVelocityShadowInvalidReason);
        var freshVelocityShadowPositionErrorMeters = hasFreshVelocityShadow
            ? Vector3.Distance(freshVelocityShadowPrediction.position, visualCameraFromBoard.position)
            : float.NaN;
        var freshImprovementMeters = hasFreshVelocityShadow
            ? rotationOnlyPositionErrorMeters - freshVelocityShadowPositionErrorMeters
            : float.NaN;
        var freshImprovementPercent = hasFreshVelocityShadow && rotationOnlyPositionErrorMeters > 0.0001f
            ? freshImprovementMeters / rotationOnlyPositionErrorMeters * 100f
            : float.NaN;
        var hasHorizontalGnssShadow = string.IsNullOrEmpty(horizontalGnssShadowInvalidReason);
        var horizontalGnssPositionErrorMeters = hasHorizontalGnssShadow
            ? Vector3.Distance(horizontalGnssShadowPrediction.position, visualCameraFromBoard.position)
            : float.NaN;
        var rotationOnlyHorizontalErrorMeters = HorizontalPositionErrorMeters(
            rotationOnlyPrediction,
            visualCameraFromBoard,
            navigationFromCameraAtReacquisition);
        var freshVelocityHorizontalErrorMeters = hasFreshVelocityShadow
            ? HorizontalPositionErrorMeters(
                freshVelocityShadowPrediction,
                visualCameraFromBoard,
                navigationFromCameraAtReacquisition)
            : float.NaN;
        var horizontalGnssHorizontalErrorMeters = hasHorizontalGnssShadow
            ? HorizontalPositionErrorMeters(
                horizontalGnssShadowPrediction,
                visualCameraFromBoard,
                navigationFromCameraAtReacquisition)
            : float.NaN;
        var hasGuardedRenderedPrediction = _hasLatestGuardedHorizontalRenderedPose;
        var guardedRenderedHorizontalErrorMeters = hasGuardedRenderedPrediction
            ? HorizontalPositionErrorMeters(
                _latestGuardedHorizontalRenderedPose,
                visualCameraFromBoard,
                navigationFromCameraAtReacquisition)
            : float.NaN;
        var gnssImprovementMeters = hasHorizontalGnssShadow
            ? rotationOnlyHorizontalErrorMeters - horizontalGnssHorizontalErrorMeters
            : float.NaN;
        var gnssImprovementPercent = hasHorizontalGnssShadow && rotationOnlyHorizontalErrorMeters > 0.0001f
            ? gnssImprovementMeters / rotationOnlyHorizontalErrorMeters * 100f
            : float.NaN;
        var hybridHorizontalErrorMeters = enableHybridHorizontalPositionShadow && hybridPositionShadow.hasTranslationCorrection
            ? HorizontalPositionErrorMeters(
                hybridPositionShadow.cameraFromBoard,
                visualCameraFromBoard,
                navigationFromCameraAtReacquisition)
            : float.NaN;
        var hybridPositionErrorMeters = enableHybridHorizontalPositionShadow && hybridPositionShadow.hasTranslationCorrection
            ? Vector3.Distance(hybridPositionShadow.cameraFromBoard.position, visualCameraFromBoard.position)
            : float.NaN;
        var bestSource = RotationPropagationRenderPolicy.SelectBestHorizontalSource(
            rotationOnlyHorizontalErrorMeters,
            freshVelocityHorizontalErrorMeters,
            horizontalGnssHorizontalErrorMeters,
            hybridHorizontalErrorMeters,
            ReacquisitionHorizontalNeutralMarginMeters);
        Debug.Log(
            "DJI_TRANSLATION_SHADOW_FRESH_REACQUISITION " +
            "mode=SHADOW_FRESH " +
            $"trustedMaxVelocityAgeMs={maximumVelocityAgeForTrustedShadowMilliseconds:F0} " +
            $"outageMs={propagationDurationMilliseconds:F1} " +
            $"integratedNorthM={freshNavigationDisplacement.x:F4} integratedEastM={freshNavigationDisplacement.y:F4} integratedDownM={freshNavigationDisplacement.z:F4} " +
            $"integratedDistanceM={freshNavigationDisplacement.magnitude:F4} " +
            $"rotationOnlyPositionErrorM={rotationOnlyPositionErrorMeters:F4} " +
            $"freshVelocityPositionErrorM={freshVelocityShadowPositionErrorMeters:F4} " +
            $"improvementM={freshImprovementMeters:F4} improvementPercent={freshImprovementPercent:F1} " +
            $"valid={hasFreshVelocityShadow} reason={(hasFreshVelocityShadow ? "NONE" : freshVelocityShadowInvalidReason)} " +
            "cameraLeverArmValidation=PENDING rendering=UNCHANGED");
        Debug.Log(
            "DJI_TRANSLATION_SHADOW_HORIZONTAL_GNSS_REACQUISITION " +
            "mode=SHADOW_HORIZONTAL_GNSS " +
            $"maxGnssSampleAgeMs={maximumGnssPositionSampleAgeForShadowMilliseconds:F0} " +
            $"outageMs={propagationDurationMilliseconds:F1} " +
            $"baselineSampleAgeMs={FormatPropagationAgeMilliseconds(horizontalGnssBaselineAgeMilliseconds)} targetSampleAgeMs={FormatPropagationAgeMilliseconds(horizontalGnssTargetAgeMilliseconds)} " +
            $"deltaNorthM={horizontalGnssNavigationDisplacement.x:F4} deltaEastM={horizontalGnssNavigationDisplacement.y:F4} deltaDownM=UNAVAILABLE " +
            $"horizontalGnssDistanceM={new Vector2(horizontalGnssNavigationDisplacement.x, horizontalGnssNavigationDisplacement.y).magnitude:F4} " +
            $"rotationOnlyPositionErrorM={rotationOnlyPositionErrorMeters:F4} freshVelocityPositionErrorM={freshVelocityShadowPositionErrorMeters:F4} horizontalGnssPositionErrorM={horizontalGnssPositionErrorMeters:F4} " +
            $"rotationOnlyHorizontalErrorM={rotationOnlyHorizontalErrorMeters:F4} freshVelocityHorizontalErrorM={freshVelocityHorizontalErrorMeters:F4} horizontalGnssHorizontalErrorM={horizontalGnssHorizontalErrorMeters:F4} " +
            $"gnssImprovementM={gnssImprovementMeters:F4} gnssImprovementPercent={gnssImprovementPercent:F1} rotationErrorDeg={rotationErrorDegrees:F2} " +
            $"valid={hasHorizontalGnssShadow} reason={(hasHorizontalGnssShadow ? "NONE" : horizontalGnssShadowInvalidReason)} " +
            "verticalTranslation=UNAVAILABLE rendering=UNCHANGED");
        var guarded = _latestGuardedHorizontalRenderSnapshot;
        Debug.Log(
            "DJI_GUARDED_HORIZONTAL_REACQUISITION " +
            $"outageMs={propagationDurationMilliseconds:F1} rotationOnlyHorizontalErrorM={rotationOnlyHorizontalErrorMeters:F4} " +
            $"gnssShadowHorizontalErrorM={FormatFinite(horizontalGnssHorizontalErrorMeters)} " +
            $"guardedRenderedHorizontalErrorM={FormatFinite(guardedRenderedHorizontalErrorMeters)} " +
            $"hybridShadowHorizontalErrorM={FormatFinite(hybridHorizontalErrorMeters)} " +
            $"horizontalCorrectionAppliedM={guarded.appliedCorrectionInNavigation.magnitude:F4} appliedAlpha={guarded.blendAlpha:F3} " +
            $"guardedHorizontalState={guarded.state} mode={guarded.renderMode} renderedPredictionAvailable={hasGuardedRenderedPrediction} " +
            $"{HorizontalRenderModeLogFields()} verticalTranslation=UNCHANGED");
        if (enableHybridHorizontalPositionShadow)
        {
            var hybrid = hybridPositionShadow.snapshot;
            Debug.Log(
                "DJI_HYBRID_POSITION_SHADOW_REACQUISITION " +
                $"outageMs={propagationDurationMilliseconds:F1} mode={FormatHybridPositionShadowMode(hybrid.mode)} " +
                $"gnssAgeMs={FormatPropagationAgeMilliseconds(hybrid.gnssAgeMilliseconds)} velocityAgeMs={FormatPropagationAgeMilliseconds(hybrid.velocityAgeMilliseconds)} " +
                $"velocityCoverageMs={FormatPropagationAgeMilliseconds(hybrid.velocityEndTimestampNs > hybrid.velocityStartTimestampNs ? (hybrid.velocityEndTimestampNs - hybrid.velocityStartTimestampNs) / 1_000_000d : double.NaN)} " +
                $"rotationOnlyHorizontalErrorM={rotationOnlyHorizontalErrorMeters:F4} freshVelocityHorizontalErrorM={FormatFinite(freshVelocityHorizontalErrorMeters)} " +
                $"gnssHorizontalErrorM={FormatFinite(horizontalGnssHorizontalErrorMeters)} hybridHorizontalErrorM={FormatFinite(hybridHorizontalErrorMeters)} " +
                $"hybridPositionErrorM={FormatFinite(hybridPositionErrorMeters)} hybridNorthM={hybrid.navigationDisplacement.x:F4} hybridEastM={hybrid.navigationDisplacement.y:F4} " +
                $"bestSource={bestSource.ToString().ToUpperInvariant()} velocityCoverageValid={hybrid.velocityCoverageValid} reason={hybrid.reason} " +
                "verticalSource=UNVALIDATED rendering=UNCHANGED");
        }
        if (!_horizontalGnssShadowStatistics.TryGetValue(bucket, out var gnssStatistics))
        {
            gnssStatistics = new HorizontalGnssShadowStatistics();
            _horizontalGnssShadowStatistics.Add(bucket, gnssStatistics);
        }
        gnssStatistics.Add(
            rotationOnlyHorizontalErrorMeters,
            hasFreshVelocityShadow,
            freshVelocityHorizontalErrorMeters,
            hasHorizontalGnssShadow,
            horizontalGnssHorizontalErrorMeters);
        if (!_hybridPositionShadowStatistics.TryGetValue(bucket, out var hybridStatistics))
        {
            hybridStatistics = new HybridPositionShadowStatistics();
            _hybridPositionShadowStatistics.Add(bucket, hybridStatistics);
        }
        hybridStatistics.Add(
            rotationOnlyHorizontalErrorMeters,
            freshVelocityHorizontalErrorMeters,
            horizontalGnssHorizontalErrorMeters,
            hybridHorizontalErrorMeters,
            hybridPositionShadow.hasTranslationCorrection);
        if (IsRotationPropagationRenderingEnabled)
        {
            Debug.Log(
                "DJI_RENDER_POSE_SOURCE=VISUAL reason=BOARD_REACQUIRED " +
                $"propagationDurationMs={propagationDurationMilliseconds:F1} " +
                $"rotationErrorAtReacquisitionDeg={rotationErrorDegrees:F2} {HorizontalRenderModeLogFields()}");
        }
        LogShadowReacquisitionStatistics();
        LogHorizontalGnssShadowStatistics();
        LogHybridPositionShadowStatistics();
    }

    private static float HorizontalPositionErrorMeters(
        Pose predictedCameraFromBoard,
        Pose visualCameraFromBoard,
        Quaternion navigationFromCamera)
    {
        var errorInNavigation = navigationFromCamera *
                                (predictedCameraFromBoard.position - visualCameraFromBoard.position);
        return new Vector2(errorInNavigation.x, errorInNavigation.y).magnitude;
    }

    private void LogVelocityScaleDiagnostic(
        ShadowRotationPropagationBaseline baseline,
        long targetTimestampNs,
        Quaternion navigationFromCameraTarget,
        Pose visualCameraFromBoard)
    {
        if (!enableVelocityTranslationShadow ||
            targetTimestampNs <= baseline.timestampNs ||
            Time.unscaledTime < _nextVelocityScaleDiagnosticLogTime)
        {
            return;
        }

        if (!TryBuildTranslationShadowPrediction(
                baseline,
                targetTimestampNs,
                navigationFromCameraTarget,
                out _,
                out var navigationDisplacement,
                out _,
                out _))
        {
            return;
        }

        _nextVelocityScaleDiagnosticLogTime = Time.unscaledTime + shadowPropagationLogIntervalSeconds;
        var rotationOnlyPrediction = ReferenceFrameTransforms.PredictCameraFromBoardRotationOnly(
            baseline.cameraFromBoardVisual,
            baseline.navigationFromCameraTelemetry,
            navigationFromCameraTarget);
        var visualEstimatedDistance = Vector3.Distance(
            rotationOnlyPrediction.position,
            visualCameraFromBoard.position);
        var ratio = visualEstimatedDistance > 0.0001f
            ? navigationDisplacement.magnitude / visualEstimatedDistance
            : float.NaN;
        Debug.Log(
            "VELOCITY_SCALE_DIAGNOSTIC " +
            $"integratedDistanceM={navigationDisplacement.magnitude:F4} " +
            $"visualEstimatedDistanceM={visualEstimatedDistance:F4} ratio={ratio:F3} " +
            "ratioDefinition=integratedOverVisual cameraLeverArmValidation=PENDING");
    }

    private void ObserveAircraftVelocityVisualValidation(
        PendingRotationDiagnosticSample pending,
        InterpolatedTelemetryRotation telemetryAtVision)
    {
        // Only a continuous four-marker HIGH_CONFIDENCE run that is already
        // driving VISUAL content may contribute. Board loss, hysteresis holds,
        // and source transitions deliberately clear the candidate history.
        if (!enableAircraftVelocityVisualValidation ||
            pending.markerCount != 4 ||
            CurrentVisualPoseQuality != VisualPoseQuality.HighConfidence ||
            _holdingVisualAuthorityForBorderlineQuality ||
            _holdingVisualAuthorityForTranslationJumpSuspect ||
            _shadowPropagationActive ||
            CurrentRenderPoseSource != DjiRenderPoseSource.Visual)
        {
            ResetAircraftVelocityVisualValidation("CONTROLLED_VISUAL_INTERVAL_UNAVAILABLE");
            return;
        }

        var current = new VelocityVisualValidationPose(
            pending.visionTimestampNs,
            pending.cameraFromBoard,
            telemetryAtVision.navigationFromCameraRotation,
            telemetryAtVision.aircraftYawDegrees,
            telemetryAtVision.gimbalPitchDegrees);
        var toleranceNs = (long)(velocityVisualValidationWindowToleranceMilliseconds * 1_000_000d);
        foreach (var windowMilliseconds in VelocityVisualValidationWindowMilliseconds)
        {
            if (_nextVelocityVisualValidationLogTimes.TryGetValue(windowMilliseconds, out var nextLogTime) &&
                Time.unscaledTime < nextLogTime)
            {
                continue;
            }

            if (!TryFindVelocityVisualValidationBaseline(
                    current.timestampNs,
                    windowMilliseconds,
                    toleranceNs,
                    out var baseline))
            {
                continue;
            }

            _nextVelocityVisualValidationLogTimes[windowMilliseconds] =
                Time.unscaledTime + diagnosticLogIntervalSeconds;
            LogAircraftVelocityVisualValidation(windowMilliseconds, baseline, current);
        }

        _velocityVisualValidationPoses.Add(current);
        if (_velocityVisualValidationPoses.Count > VelocityVisualValidationHistoryCapacity)
            _velocityVisualValidationPoses.RemoveAt(0);
    }

    private void ObserveAircraftPositionVisualValidation(
        PendingRotationDiagnosticSample pending,
        InterpolatedTelemetryRotation telemetryAtVision)
    {
        if (!enableAircraftPositionVisualValidation ||
            pending.markerCount != 4 ||
            CurrentVisualPoseQuality != VisualPoseQuality.HighConfidence ||
            _holdingVisualAuthorityForBorderlineQuality ||
            _holdingVisualAuthorityForTranslationJumpSuspect ||
            _shadowPropagationActive ||
            CurrentRenderPoseSource != DjiRenderPoseSource.Visual ||
            _telemetryProvider == null)
        {
            ResetAircraftPositionVisualValidation();
            return;
        }

        var current = new PositionVisualValidationPose(
            pending.visionTimestampNs,
            pending.cameraFromBoard,
            telemetryAtVision.navigationFromCameraRotation);
        if (!_hasPositionVisualBaseline && TryGetPositionSampleAtOrBefore(current.timestampNs, out var baselinePosition))
        {
            _hasPositionVisualBaseline = true;
            Debug.Log(
                "DJI_POSITION_VISUAL_BASELINE_CAPTURED " +
                $"baselineTimestampNs={current.timestampNs} positionTimestampNs={baselinePosition.timestampNs} " +
                "frame=LOCAL_NED_HORIZONTAL_ONLY altitudeReferenceValidation=PENDING rendering=UNCHANGED");
        }
        var toleranceNs = (long)(velocityVisualValidationWindowToleranceMilliseconds * 1_000_000d);
        foreach (var windowMilliseconds in PositionVisualValidationWindowMilliseconds)
        {
            if (_nextPositionVisualValidationLogTimes.TryGetValue(windowMilliseconds, out var nextLogTime) &&
                Time.unscaledTime < nextLogTime)
            {
                continue;
            }

            if (!TryFindPositionVisualValidationBaseline(current.timestampNs, windowMilliseconds, toleranceNs, out var baseline))
                continue;

            _nextPositionVisualValidationLogTimes[windowMilliseconds] =
                Time.unscaledTime + diagnosticLogIntervalSeconds;
            LogAircraftPositionVisualValidation(windowMilliseconds, baseline, current);
        }

        _positionVisualValidationPoses.Add(current);
        if (_positionVisualValidationPoses.Count > PositionVisualValidationHistoryCapacity)
            _positionVisualValidationPoses.RemoveAt(0);
    }

    private bool TryFindPositionVisualValidationBaseline(
        long targetTimestampNs,
        int targetWindowMilliseconds,
        long toleranceNs,
        out PositionVisualValidationPose baseline)
    {
        baseline = default;
        var desiredDurationNs = targetWindowMilliseconds * 1_000_000L;
        var bestErrorNs = long.MaxValue;
        foreach (var candidate in _positionVisualValidationPoses)
        {
            if (candidate.timestampNs >= targetTimestampNs)
                continue;

            var durationErrorNs = Math.Abs(targetTimestampNs - candidate.timestampNs - desiredDurationNs);
            if (durationErrorNs > toleranceNs || durationErrorNs >= bestErrorNs)
                continue;

            baseline = candidate;
            bestErrorNs = durationErrorNs;
        }
        return bestErrorNs != long.MaxValue;
    }

    private void LogAircraftPositionVisualValidation(
        int requestedWindowMilliseconds,
        PositionVisualValidationPose baseline,
        PositionVisualValidationPose current)
    {
        if (!TryGetPositionSampleAtOrBefore(baseline.timestampNs, out var baselinePosition) ||
            !TryGetPositionSampleAtOrBefore(current.timestampNs, out var currentPosition))
        {
            Debug.Log("DJI_POSITION_SOURCE_STATUS horizontalValid=false verticalValid=false verticalSource=UNAVAILABLE reason=POSITION_SAMPLE_UNAVAILABLE");
            return;
        }

        var baselineAgeMs = (baseline.timestampNs - baselinePosition.timestampNs) / 1_000_000d;
        var targetAgeMs = (current.timestampNs - currentPosition.timestampNs) / 1_000_000d;
        var maximumAgeMs = Math.Max(baselineAgeMs, targetAgeMs);
        if (maximumAgeMs > maximumAircraftPositionSampleAgeMilliseconds)
        {
            Debug.Log(
                "DJI_POSITION_VISUAL_VALIDATION " +
                $"targetWindowMs={requestedWindowMilliseconds} valid=false reason=POSITION_SAMPLE_STALE " +
                $"baselineSampleAgeMs={baselineAgeMs:F1} sampleAgeMs={targetAgeMs:F1}");
            return;
        }

        var visual = ReferenceFrameTransforms.CameraDisplacementInNavigationFromStationaryBoard(
            baseline.cameraFromBoard,
            baseline.navigationFromCamera,
            current.cameraFromBoard,
            current.navigationFromCamera);
        var gnssHorizontal = DjiGeodesy.HorizontalNedDelta(
            baselinePosition.latitudeDegrees,
            baselinePosition.longitudeDegrees,
            currentPosition.latitudeDegrees,
            currentPosition.longitudeDegrees);
        var gnss = new Vector3(gnssHorizontal.x, gnssHorizontal.y, 0f);
        var visualDistance = new Vector2(visual.x, visual.y).magnitude;
        var gnssDistance = gnssHorizontal.magnitude;
        var meaningful = visualDistance >= positionVisualValidationMinimumDistanceMeters;
        var scaleRatio = meaningful ? gnssDistance / visualDistance : float.NaN;
        var directionError = meaningful && gnssDistance > 0.0001f
            ? Vector2.Angle(new Vector2(visual.x, visual.y), gnssHorizontal)
            : float.NaN;
        var durationMs = (current.timestampNs - baseline.timestampNs) / 1_000_000d;
        Debug.Log("DJI_POSITION_SOURCE_STATUS horizontalValid=true verticalValid=false verticalSource=UNAVAILABLE altitudeReferenceValidation=PENDING");
        Debug.Log(
            "DJI_POSITION_VISUAL_VALIDATION " +
            $"targetWindowMs={requestedWindowMilliseconds} durationMs={durationMs:F1} " +
            $"visualN={visual.x:F4} visualE={visual.y:F4} " +
            $"gnssN={gnss.x:F4} gnssE={gnss.y:F4} " +
            $"horizontalVisualDistanceM={visualDistance:F4} horizontalGnssDistanceM={gnssDistance:F4} " +
            $"scaleRatio={FormatFinite(scaleRatio)} directionErrorDeg={FormatFinite(directionError)} " +
            $"positionSampleCount={_telemetryProvider.AircraftPositionHistoryCount} " +
            $"baselineSampleAgeMs={baselineAgeMs:F1} sampleAgeMs={targetAgeMs:F1} " +
            "downM=UNAVAILABLE altitudeReferenceValidation=PENDING valid=true");
    }

    private bool TryGetPositionSampleAtOrBefore(long timestampNs, out TelemetryAircraftPositionSample sample)
    {
        sample = default;
        if (_telemetryProvider == null)
            return false;

        var found = false;
        foreach (var candidate in _telemetryProvider.AircraftPositionHistory)
        {
            if (candidate.timestampNs > timestampNs)
                break;
            sample = candidate;
            found = true;
        }
        return found;
    }

    private bool TryGetFreshPositionSampleAtOrBefore(
        long timestampNs,
        out TelemetryAircraftPositionSample sample,
        out double ageMilliseconds,
        out string reason)
    {
        sample = default;
        ageMilliseconds = double.PositiveInfinity;
        if (!TryGetPositionSampleAtOrBefore(timestampNs, out sample))
        {
            reason = "NO_SAMPLE";
            return false;
        }

        if (!DjiAircraftPositionFreshness.IsFreshAtOrBefore(
                sample.timestampNs,
                timestampNs,
                maximumGnssPositionSampleAgeForShadowMilliseconds,
                out ageMilliseconds))
        {
            reason = "TARGET_STALE";
            return false;
        }

        reason = "NONE";
        return true;
    }

    private void ResetAircraftPositionVisualValidation()
    {
        if (_positionVisualValidationPoses.Count == 0)
            return;

        _positionVisualValidationPoses.Clear();
        _nextPositionVisualValidationLogTimes.Clear();
        _hasPositionVisualBaseline = false;
    }

    private bool TryFindVelocityVisualValidationBaseline(
        long targetTimestampNs,
        int targetWindowMilliseconds,
        long toleranceNs,
        out VelocityVisualValidationPose baseline)
    {
        baseline = default;
        var desiredDurationNs = targetWindowMilliseconds * 1_000_000L;
        var bestErrorNs = long.MaxValue;
        foreach (var candidate in _velocityVisualValidationPoses)
        {
            if (candidate.timestampNs >= targetTimestampNs)
                continue;

            var durationErrorNs = Math.Abs(
                targetTimestampNs - candidate.timestampNs - desiredDurationNs);
            if (durationErrorNs > toleranceNs || durationErrorNs >= bestErrorNs)
                continue;

            baseline = candidate;
            bestErrorNs = durationErrorNs;
        }

        return bestErrorNs != long.MaxValue;
    }

    private void LogAircraftVelocityVisualValidation(
        int targetWindowMilliseconds,
        VelocityVisualValidationPose baseline,
        VelocityVisualValidationPose current)
    {
        var durationMilliseconds = (current.timestampNs - baseline.timestampNs) / 1_000_000d;
        var visualDisplacement = ReferenceFrameTransforms.CameraDisplacementInNavigationFromStationaryBoard(
            baseline.cameraFromBoard,
            baseline.navigationFromCamera,
            current.cameraFromBoard,
            current.navigationFromCamera);
        var visualDistance = visualDisplacement.magnitude;
        var maximumSampleAgeNs = (long)(maximumVelocitySampleAgeForShadowMilliseconds * 1_000_000d);
        var velocityDisplacement = Vector3.zero;
        var velocityCoverage = default(ReferenceFrameTransforms.VelocityStateHistoryCoverage);
        var velocityReason = "VELOCITY_PROVIDER_UNAVAILABLE";
        var velocityCoverageValid = _telemetryProvider != null &&
                                    ReferenceFrameTransforms.TryIntegrateNavigationVelocityStateHistory(
                                        _telemetryProvider.TelemetryVelocityHistory,
                                        baseline.timestampNs,
                                        current.timestampNs,
                                        maximumSampleAgeNs,
                                        out velocityDisplacement,
                                        out velocityCoverage,
                                        out velocityReason);

        var trustedMaximumSampleAgeNs = (long)(maximumVelocityAgeForTrustedShadowMilliseconds * 1_000_000d);
        var freshVelocityDisplacement = Vector3.zero;
        var freshVelocityCoverage = default(ReferenceFrameTransforms.VelocityStateHistoryCoverage);
        var freshVelocityReason = "VELOCITY_PROVIDER_UNAVAILABLE";
        var freshVelocityCoverageValid = _telemetryProvider != null &&
                                         ReferenceFrameTransforms.TryIntegrateNavigationVelocityStateHistory(
                                             _telemetryProvider.TelemetryVelocityHistory,
                                             baseline.timestampNs,
                                             current.timestampNs,
                                             trustedMaximumSampleAgeNs,
                                             out freshVelocityDisplacement,
                                             out freshVelocityCoverage,
                                             out freshVelocityReason);

        var velocityDistance = velocityDisplacement.magnitude;
        var aircraftYawDeltaDegrees = Mathf.Abs(Mathf.DeltaAngle(
            baseline.aircraftYawDegrees,
            current.aircraftYawDegrees));
        var gimbalPitchDeltaDegrees = Mathf.Abs(Mathf.DeltaAngle(
            baseline.gimbalPitchDegrees,
            current.gimbalPitchDegrees));
        var cameraRotationDeltaDegrees = Quaternion.Angle(
            baseline.navigationFromCamera,
            current.navigationFromCamera);
        var motionClass = ClassifyVelocityVisualMotion(
            visualDistance,
            velocityDistance,
            aircraftYawDeltaDegrees,
            gimbalPitchDeltaDegrees);
        var meaningfulVisualMotion = visualDistance >= velocityVisualValidationMinimumDistanceMeters;
        var scaleRatio = meaningfulVisualMotion && velocityCoverageValid
            ? velocityDistance / visualDistance
            : float.NaN;
        var directionAgreementDegrees = meaningfulVisualMotion && velocityCoverageValid &&
                                        velocityDistance > velocityVisualValidationNearZeroVelocityDistanceMeters
            ? Vector3.Angle(visualDisplacement, velocityDisplacement)
            : float.NaN;
        var error = velocityCoverageValid
            ? velocityDisplacement - visualDisplacement
            : new Vector3(float.NaN, float.NaN, float.NaN);
        var reportedVelocityDisplacement = velocityCoverageValid
            ? velocityDisplacement
            : new Vector3(float.NaN, float.NaN, float.NaN);
        var heldVelocity = TryGetHeldVelocityAtTimestamp(current.timestampNs, out var targetHeldVelocity)
            ? targetHeldVelocity
            : new Vector3(float.NaN, float.NaN, float.NaN);
        var scaleClassification = meaningfulVisualMotion
            ? "MEANINGFUL"
            : "TOO_LITTLE_MOTION_FOR_SCALE_VALIDATION";
        var sparseZeroDisagreement = meaningfulVisualMotion && velocityCoverageValid &&
                                     velocityDistance <= velocityVisualValidationNearZeroVelocityDistanceMeters;
        var freshVelocityDistance = freshVelocityDisplacement.magnitude;
        var freshScaleRatio = meaningfulVisualMotion && freshVelocityCoverageValid
            ? freshVelocityDistance / visualDistance
            : float.NaN;
        var freshDirectionAgreementDegrees = meaningfulVisualMotion && freshVelocityCoverageValid &&
                                                freshVelocityDistance > velocityVisualValidationNearZeroVelocityDistanceMeters
            ? Vector3.Angle(visualDisplacement, freshVelocityDisplacement)
            : float.NaN;

        _velocityVisualValidationStatistics.Add(
            motionClass,
            meaningfulVisualMotion,
            velocityCoverageValid,
            visualDisplacement,
            velocityDisplacement,
            velocityVisualValidationNearZeroVelocityDistanceMeters,
            velocityVisualValidationMinimumAxisDistanceMeters);

        Debug.Log(
            "DJI_VELOCITY_VISUAL_VALIDATION " +
            $"targetWindowMs={targetWindowMilliseconds} durationMs={durationMilliseconds:F1} " +
            $"visualN={visualDisplacement.x:F4} visualE={visualDisplacement.y:F4} visualD={visualDisplacement.z:F4} " +
            $"velocityN={FormatFinite(reportedVelocityDisplacement.x)} velocityE={FormatFinite(reportedVelocityDisplacement.y)} velocityD={FormatFinite(reportedVelocityDisplacement.z)} " +
            $"errorN={FormatFinite(error.x)} errorE={FormatFinite(error.y)} errorD={FormatFinite(error.z)} " +
            $"visualDistanceM={visualDistance:F4} velocityDistanceM={FormatFinite(velocityCoverageValid ? velocityDistance : float.NaN)} " +
            $"scaleRatio={FormatFinite(scaleRatio)} directionAgreementDeg={FormatFinite(directionAgreementDegrees)} " +
            $"velocityCallbackCount={velocityCoverage.callbackCountInsideInterval} " +
            $"oldestHeldSampleAgeMs={velocityCoverage.BaselineSampleAgeMilliseconds(baseline.timestampNs):F1} " +
            $"targetHeldSampleAgeMs={velocityCoverage.TargetSampleAgeMilliseconds(current.timestampNs):F1} " +
            $"heldVelocityN={FormatFinite(heldVelocity.x)} heldVelocityE={FormatFinite(heldVelocity.y)} heldVelocityD={FormatFinite(heldVelocity.z)} " +
            $"aircraftYawDeltaDeg={aircraftYawDeltaDegrees:F2} gimbalPitchDeltaDeg={gimbalPitchDeltaDegrees:F2} " +
            $"cameraRotationDeltaDeg={cameraRotationDeltaDegrees:F2} motionClass={motionClass} " +
            $"scaleClassification={scaleClassification} sparseZeroDisagreement={sparseZeroDisagreement} " +
            $"velocityCoverageValid={velocityCoverageValid} reason={(velocityCoverageValid ? "NONE" : velocityReason)} " +
            $"freshMaxVelocityAgeMs={maximumVelocityAgeForTrustedShadowMilliseconds:F0} " +
            $"freshVelocityN={FormatFinite(freshVelocityCoverageValid ? freshVelocityDisplacement.x : float.NaN)} " +
            $"freshVelocityE={FormatFinite(freshVelocityCoverageValid ? freshVelocityDisplacement.y : float.NaN)} " +
            $"freshVelocityD={FormatFinite(freshVelocityCoverageValid ? freshVelocityDisplacement.z : float.NaN)} " +
            $"freshVelocityDistanceM={FormatFinite(freshVelocityCoverageValid ? freshVelocityDistance : float.NaN)} " +
            $"freshScaleRatio={FormatFinite(freshScaleRatio)} freshDirectionAgreementDeg={FormatFinite(freshDirectionAgreementDegrees)} " +
            $"freshVelocityCallbackCount={freshVelocityCoverage.callbackCountInsideInterval} " +
            $"freshOldestHeldSampleAgeMs={freshVelocityCoverage.BaselineSampleAgeMilliseconds(baseline.timestampNs):F1} " +
            $"freshTargetHeldSampleAgeMs={freshVelocityCoverage.TargetSampleAgeMilliseconds(current.timestampNs):F1} " +
            $"freshVelocityCoverageValid={freshVelocityCoverageValid} freshReason={(freshVelocityCoverageValid ? "NONE" : freshVelocityReason)}");

        if (Time.unscaledTime >= _nextVelocityVisualValidationSummaryLogTime)
        {
            _nextVelocityVisualValidationSummaryLogTime = Time.unscaledTime + diagnosticLogIntervalSeconds;
            Debug.Log("DJI_VELOCITY_VISUAL_VALIDATION_SUMMARY " + _velocityVisualValidationStatistics.Summary());
        }
    }

    private VelocityVisualMotionClass ClassifyVelocityVisualMotion(
        float visualDistance,
        float velocityDistance,
        float aircraftYawDeltaDegrees,
        float gimbalPitchDeltaDegrees)
    {
        var maximumRawRotationDeltaDegrees = Mathf.Max(aircraftYawDeltaDegrees, gimbalPitchDeltaDegrees);
        if (visualDistance >= velocityVisualValidationMinimumDistanceMeters &&
            maximumRawRotationDeltaDegrees <= velocityVisualValidationTranslationDominantRotationDegrees)
        {
            return VelocityVisualMotionClass.TranslationDominant;
        }

        if (velocityDistance <= velocityVisualValidationNearZeroVelocityDistanceMeters &&
            maximumRawRotationDeltaDegrees >= velocityVisualValidationRotationDominantDegrees)
        {
            return VelocityVisualMotionClass.RotationDominant;
        }

        return VelocityVisualMotionClass.Mixed;
    }

    private bool TryGetHeldVelocityAtTimestamp(long timestampNs, out Vector3 velocity)
    {
        velocity = Vector3.zero;
        if (_telemetryProvider == null)
            return false;

        var found = false;
        foreach (var sample in _telemetryProvider.TelemetryVelocityHistory)
        {
            if (sample.timestampNs > timestampNs)
                break;

            velocity = sample.navigationVelocityMetersPerSecond;
            found = true;
        }

        return found;
    }

    private void ResetAircraftVelocityVisualValidation(string reason)
    {
        if (_velocityVisualValidationPoses.Count > 0)
        {
            _velocityVisualValidationPoses.Clear();
            _nextVelocityVisualValidationLogTimes.Clear();
            Debug.Log($"DJI_VELOCITY_VISUAL_VALIDATION_RESET reason={reason}");
        }
        ResetAircraftPositionVisualValidation();
    }

    private static string FormatFinite(float value) => float.IsFinite(value) ? value.ToString("F4") : "UNAVAILABLE";

    private void LogShadowReacquisitionStatistics()
    {
        foreach (ReacquisitionDurationBucket bucket in Enum.GetValues(typeof(ReacquisitionDurationBucket)))
        {
            if (!_shadowReacquisitionStatistics.TryGetValue(bucket, out var statistics) || statistics.Count == 0)
                continue;

            Debug.Log(
                "ROTATION_PROPAGATION_REACQUISITION_BUCKET " +
                $"durationBucket={FormatReacquisitionDurationBucket(bucket)} sampleCount={statistics.Count} " +
                $"meanRotationErrorDeg={statistics.MeanDegrees:F2} medianRotationErrorDeg={statistics.MedianDegrees:F2} " +
                $"p95RotationErrorDeg={statistics.P95Degrees:F2}");
        }
    }

    private void LogHorizontalGnssShadowStatistics()
    {
        foreach (ReacquisitionDurationBucket bucket in Enum.GetValues(typeof(ReacquisitionDurationBucket)))
        {
            if (!_horizontalGnssShadowStatistics.TryGetValue(bucket, out var statistics) ||
                statistics.TotalComparisons == 0)
            {
                continue;
            }

            Debug.Log(
                "DJI_TRANSLATION_SHADOW_HORIZONTAL_GNSS_BUCKET " +
                $"durationBucket={FormatReacquisitionDurationBucket(bucket)} {statistics.Summary()} " +
                "primaryMetric=HORIZONTAL_ERROR_ONLY verticalTranslation=UNAVAILABLE rendering=UNCHANGED");
        }
    }

    private void LogHybridPositionShadowStatistics()
    {
        foreach (ReacquisitionDurationBucket bucket in Enum.GetValues(typeof(ReacquisitionDurationBucket)))
        {
            if (!_hybridPositionShadowStatistics.TryGetValue(bucket, out var statistics) ||
                statistics.EventCount == 0)
            {
                continue;
            }

            Debug.Log(
                "DJI_HYBRID_POSITION_SHADOW_BUCKET " +
                $"durationBucket={FormatReacquisitionDurationBucket(bucket)} {statistics.Summary()} " +
                "primaryMetric=HORIZONTAL_ERROR_ONLY verticalSource=UNVALIDATED rendering=UNCHANGED");
        }
    }

    private void ResetShadowRotationPropagation(string reason)
    {
        // The long-lived GNSS diagnostic intentionally survives only the
        // normal production timeout. All other resets end the diagnostic
        // outage because its immutable baseline is no longer trustworthy.
        if (reason != "PROPAGATION_TIMEOUT")
            _worldLocalizationShadow?.ResetHorizontalGnssWorldShadowOutage();

        if (!_hasShadowRotationPropagationBaseline && !_shadowPropagationActive && !_hasLatestShadowPrediction &&
            !_hasLatestTranslationShadowPrediction &&
            !_hasLatestHybridPositionShadow &&
            _propagationStartTimestampNs == RotationPropagationTiming.NoTimestamp &&
            _translationShadowStartTimestampNs == RotationPropagationTiming.NoTimestamp &&
            !_hasPendingReacquisition)
            return;

        _hasShadowRotationPropagationBaseline = false;
        StopRenderedPropagation();
        _hasPendingReacquisition = false;
        _pendingReacquisitionStartTimestampNs = RotationPropagationTiming.NoTimestamp;
        ClearVisualReacquisitionCandidate();
        _visualReacquisitionMode = VisualReacquisitionMode.None;
        Debug.Log($"DJI_ROTATION_PROPAGATION_SHADOW baseline=RESET reason={reason}");
    }

    private static ReacquisitionDurationBucket GetReacquisitionDurationBucket(double durationMilliseconds)
    {
        if (durationMilliseconds < 500d)
            return ReacquisitionDurationBucket.Under500Milliseconds;
        if (durationMilliseconds < 1000d)
            return ReacquisitionDurationBucket.From500MillisecondsTo1Second;
        if (durationMilliseconds < 2000d)
            return ReacquisitionDurationBucket.From1To2Seconds;
        return durationMilliseconds <= 5000d
            ? ReacquisitionDurationBucket.From2To5Seconds
            : ReacquisitionDurationBucket.Over5Seconds;
    }

    private static string FormatReacquisitionDurationBucket(ReacquisitionDurationBucket bucket) => bucket switch
    {
        ReacquisitionDurationBucket.Under500Milliseconds => "<0.5s",
        ReacquisitionDurationBucket.From500MillisecondsTo1Second => "0.5-1s",
        ReacquisitionDurationBucket.From1To2Seconds => "1-2s",
        ReacquisitionDurationBucket.From2To5Seconds => "2-5s",
        _ => ">5s"
    };

    private void EnsureTelemetryRotationSubscription()
    {
        _telemetryProvider ??= FindFirstObjectByType<DjiCameraPoseProvider>();
        if (_telemetryProviderWithShadowSubscription == _telemetryProvider)
            return;

        if (_telemetryProviderWithShadowSubscription != null)
        {
            _telemetryProviderWithShadowSubscription.TelemetryOrientationRecorded -= OnTelemetryOrientationRecorded;
            _telemetryProviderWithShadowSubscription.TelemetryVelocityRecorded -= OnTelemetryVelocityRecorded;
        }

        _telemetryProviderWithShadowSubscription = _telemetryProvider;
        if (_telemetryProviderWithShadowSubscription != null)
        {
            _telemetryProviderWithShadowSubscription.TelemetryOrientationRecorded += OnTelemetryOrientationRecorded;
            _telemetryProviderWithShadowSubscription.TelemetryVelocityRecorded += OnTelemetryVelocityRecorded;
        }
    }

    private void OnTelemetryOrientationRecorded(DjiCameraPoseProvider.TelemetryOrientationSample sample)
    {
        if (!sample.hasOrientation)
        {
            ResetShadowRotationPropagation("TELEMETRY_ORIENTATION_INVALID");
            return;
        }

        UpdateShadowPrediction(sample.timestampNs, sample.navigationFromCameraRotation);
        ProcessPendingRotationDiagnosticSamples(sample.timestampNs);
        TryCompleteVisualReacquisitionHandover();
    }

    private void OnTelemetryVelocityRecorded(DjiCameraPoseProvider.TelemetryVelocitySample sample)
    {
        // This event only refreshes the diagnostic translation shadow. The
        // shared cube continues to use the existing render-pose resolver.
        UpdateTranslationShadowPredictionAtLatestComparableTimestamp();
    }

    private void ProcessPendingRotationDiagnosticSamples(long nowTimestampNs)
    {
        if (_pendingRotationDiagnosticSamples.Count == 0)
            return;

        // Preserve acquisition order: the first successfully paired sample
        // defines C0, and later samples are always compared against it.
        for (var index = 0; index < _pendingRotationDiagnosticSamples.Count;)
        {
            var pending = _pendingRotationDiagnosticSamples[index];
            if (pending.baselineId != 0 && pending.baselineId != _rotationComparisonBaselineId)
            {
                Debug.Log(
                    $"ROTATION_DIAGNOSTIC_SAMPLE_DROPPED reason=BASELINE_CHANGED " +
                    $"visionTimestampNs={pending.visionTimestampNs}");
                _pendingRotationDiagnosticSamples.RemoveAt(index);
                continue;
            }

            if (TryInterpolateTelemetryRotation(pending.visionTimestampNs, out var telemetryAtVision, out _, "PROPAGATION_BASELINE"))
            {
                CompleteVisualTelemetryRotationComparison(pending, telemetryAtVision);
                _pendingRotationDiagnosticSamples.RemoveAt(index);
                continue;
            }

            var pendingAgeNs = nowTimestampNs - pending.enqueuedTimestampNs;
            if (pendingAgeNs < pendingTelemetryBracketTimeoutSeconds * 1_000_000_000.0)
            {
                index++;
                continue;
            }

            Debug.Log(
                $"ROTATION_DIAGNOSTIC_SAMPLE_DROPPED reason=TELEMETRY_BRACKET_TIMEOUT " +
                $"visionTimestampNs={pending.visionTimestampNs} pendingAgeMs={pendingAgeNs / 1_000_000.0:F2}");
            _pendingRotationDiagnosticSamples.RemoveAt(index);
            continue;
        }
    }

    private void CompleteVisualTelemetryRotationComparison(
        PendingRotationDiagnosticSample pending,
        InterpolatedTelemetryRotation telemetryAtVision)
    {
        var pairedSample = new PairedRotationDiagnosticSample(
            pending.visionTimestampNs,
            pending.cameraFromBoard.rotation,
            telemetryAtVision.navigationFromCameraRotation,
            telemetryAtVision.aircraftYawDegrees,
            telemetryAtVision.gimbalPitchDegrees);
        if (compareVisualAndTelemetryRotation)
            ObservePairedRotationDiagnosticSample(pairedSample, pending.shouldLogDiagnosticSnapshot);
        ObserveAircraftVelocityVisualValidation(pending, telemetryAtVision);
        ObserveAircraftPositionVisualValidation(pending, telemetryAtVision);
        CaptureShadowBaselineOrCompareReacquisition(pending, telemetryAtVision);

        if (!compareVisualAndTelemetryRotation)
            return;

        if (!_hasRotationComparisonBaseline)
        {
            _rotationComparisonBaseline = new RotationComparisonBaseline(
                pending.visionTimestampNs,
                pending.cameraFromBoard.rotation,
                telemetryAtVision.navigationFromCameraRotation);
            _rotationComparisonBaselineId++;
            _baselineTelemetryRotationsByOffset.Clear();
            _baselineTelemetryRotationsByOffset.Add(0, telemetryAtVision.navigationFromCameraRotation);
            _hasRotationComparisonBaseline = true;
            Debug.Log(
                $"DJI_ROTATION_SYNC visionTimestampNs={pending.visionTimestampNs} " +
                $"telemetryBeforeNs={telemetryAtVision.beforeTimestampNs} telemetryAfterNs={telemetryAtVision.afterTimestampNs} " +
                $"interpolationAlpha={telemetryAtVision.interpolationAlpha:F3} nearestDeltaMs={telemetryAtVision.nearestDeltaMilliseconds:F2} " +
                "baseline=ESTABLISHED");
            return;
        }

        // Both deltas map vectors from the baseline camera frame C0 into the
        // current camera frame C1. The unknown NFromBoard cancels out.
        var visualCamera1FromCamera0 = Quaternion.Normalize(
            pending.cameraFromBoard.rotation * Quaternion.Inverse(_rotationComparisonBaseline.cameraFromBoardRotation));
        var telemetryCamera1FromCamera0 = Quaternion.Normalize(
            Quaternion.Inverse(telemetryAtVision.navigationFromCameraRotation) *
            _rotationComparisonBaseline.navigationFromCameraRotation);
        var rotationError = Quaternion.Angle(visualCamera1FromCamera0, telemetryCamera1FromCamera0);
        var elapsedMilliseconds = (pending.visionTimestampNs - _rotationComparisonBaseline.visionTimestampNs) / 1_000_000.0;

        if (pending.shouldLogDiagnosticSnapshot)
        {
            Debug.Log(
                $"DJI_ROTATION_SYNC visionTimestampNs={pending.visionTimestampNs} " +
                $"telemetryBeforeNs={telemetryAtVision.beforeTimestampNs} telemetryAfterNs={telemetryAtVision.afterTimestampNs} " +
                $"interpolationAlpha={telemetryAtVision.interpolationAlpha:F3} nearestDeltaMs={telemetryAtVision.nearestDeltaMilliseconds:F2}");
            Debug.Log(
                $"TELEMETRY_ROTATION_COMPARISON visualDeltaDeg={Quaternion.Angle(Quaternion.identity, visualCamera1FromCamera0):F2} " +
                $"visualDelta={FormatRotation(visualCamera1FromCamera0)} " +
                $"telemetryDeltaDeg={Quaternion.Angle(Quaternion.identity, telemetryCamera1FromCamera0):F2} " +
                $"telemetryDelta={FormatRotation(telemetryCamera1FromCamera0)} errorDeg={rotationError:F2} " +
                $"elapsedMsFromBaseline={elapsedMilliseconds:F1}");
        }

        if (Quaternion.Angle(Quaternion.identity, visualCamera1FromCamera0) < minimumRelativeRotationForOffsetScanDegrees)
            return;

        foreach (var offsetMilliseconds in RotationOffsetScanMilliseconds)
        {
            if (!TryGetRelativeTelemetryRotationForOffset(offsetMilliseconds, pending.visionTimestampNs, out var telemetryRelativeRotation))
                continue;

            var errorDegrees = Quaternion.Angle(visualCamera1FromCamera0, telemetryRelativeRotation);
            if (!_rotationOffsetStatistics.TryGetValue(offsetMilliseconds, out var statistics))
            {
                statistics = new RotationErrorStatistics();
                _rotationOffsetStatistics.Add(offsetMilliseconds, statistics);
            }
            statistics.Add(errorDegrees);
        }

        if (pending.shouldLogDiagnosticSnapshot)
            LogRotationOffsetScan();
    }

    private void ObservePairedRotationDiagnosticSample(
        PairedRotationDiagnosticSample current,
        bool shouldLogDiagnosticSnapshot)
    {
        if (!_hasLastPairedRotationDiagnosticSample)
        {
            _lastPairedRotationDiagnosticSample = current;
            _stationaryRotationDiagnosticReference = current;
            _hasLastPairedRotationDiagnosticSample = true;
            _pairedRotationDiagnosticSamples.Add(current);
            return;
        }

        var stepYawDegrees = Mathf.Abs(Mathf.DeltaAngle(
            _lastPairedRotationDiagnosticSample.aircraftYawDegrees,
            current.aircraftYawDegrees));
        var stepPitchDegrees = Mathf.Abs(Mathf.DeltaAngle(
            _lastPairedRotationDiagnosticSample.gimbalPitchDegrees,
            current.gimbalPitchDegrees));
        if (stepYawDegrees <= stationaryAxisDeltaDegrees && stepPitchDegrees <= stationaryAxisDeltaDegrees)
            _stationaryRotationDiagnosticReference = current;

        _pairedRotationDiagnosticSamples.Add(current);
        const long retainedHistoryNs = 6_000_000_000L;
        while (_pairedRotationDiagnosticSamples.Count > 0 &&
               current.timestampNs - _pairedRotationDiagnosticSamples[0].timestampNs > retainedHistoryNs)
        {
            _pairedRotationDiagnosticSamples.RemoveAt(0);
        }

        RecordRotationErrorByHorizon(current);
        RecordControlledRotationTest(ControlledRotationTest.Yaw, current, shouldLogDiagnosticSnapshot);
        RecordControlledRotationTest(ControlledRotationTest.GimbalPitch, current, shouldLogDiagnosticSnapshot);
        RecordYawValidationLeg(current, shouldLogDiagnosticSnapshot);
        _lastPairedRotationDiagnosticSample = current;
    }

    private void RecordRotationErrorByHorizon(PairedRotationDiagnosticSample current)
    {
        foreach (var horizonMilliseconds in RotationErrorHorizonsMilliseconds)
        {
            if (!TryGetPairedSampleAtHorizon(current.timestampNs, horizonMilliseconds, out var earlier))
                continue;

            var visualRelative = RelativeVisualRotation(earlier, current);
            var telemetryRelative = RelativeTelemetryRotation(earlier, current);
            if (!_rotationErrorByHorizonStatistics.TryGetValue(horizonMilliseconds, out var statistics))
            {
                statistics = new RotationErrorStatistics();
                _rotationErrorByHorizonStatistics.Add(horizonMilliseconds, statistics);
            }
            statistics.Add(Quaternion.Angle(visualRelative, telemetryRelative));
        }
    }

    private bool TryGetPairedSampleAtHorizon(
        long currentTimestampNs,
        int horizonMilliseconds,
        out PairedRotationDiagnosticSample earlier)
    {
        earlier = default;
        var targetTimestampNs = currentTimestampNs - horizonMilliseconds * 1_000_000L;
        var maximumMatchDistanceNs = Math.Max(150_000_000L, horizonMilliseconds * 350_000L);
        var bestDistanceNs = long.MaxValue;
        foreach (var sample in _pairedRotationDiagnosticSamples)
        {
            if (sample.timestampNs > currentTimestampNs)
                continue;

            var distanceNs = Math.Abs(sample.timestampNs - targetTimestampNs);
            if (distanceNs >= bestDistanceNs)
                continue;

            bestDistanceNs = distanceNs;
            earlier = sample;
        }

        return bestDistanceNs <= maximumMatchDistanceNs;
    }

    private void RecordControlledRotationTest(
        ControlledRotationTest test,
        PairedRotationDiagnosticSample current,
        bool shouldLogDiagnosticSnapshot)
    {
        if (!_controlledRotationTestStatistics.TryGetValue(test, out var statistics))
        {
            statistics = new ControlledRotationTestStatistics(test);
            _controlledRotationTestStatistics.Add(test, statistics);
        }

        var primaryDeltaFromStationary = test == ControlledRotationTest.Yaw
            ? SignedTelemetryYawDegrees(_stationaryRotationDiagnosticReference, current)
            : Mathf.DeltaAngle(_stationaryRotationDiagnosticReference.gimbalPitchDegrees, current.gimbalPitchDegrees);
        var crossAxisDeltaFromStationary = test == ControlledRotationTest.Yaw
            ? Mathf.DeltaAngle(_stationaryRotationDiagnosticReference.gimbalPitchDegrees, current.gimbalPitchDegrees)
            : Mathf.DeltaAngle(_stationaryRotationDiagnosticReference.aircraftYawDegrees, current.aircraftYawDegrees);

        if (!statistics.HasBaseline)
        {
            if (Mathf.Abs(primaryDeltaFromStationary) < controlledTestStartDegrees ||
                Mathf.Abs(crossAxisDeltaFromStationary) > controlledTestCrossAxisLimitDegrees)
            {
                return;
            }

            statistics.Reset();
            statistics.Baseline = _stationaryRotationDiagnosticReference;
            statistics.HasBaseline = true;
            Debug.Log(
                $"{ControlledTestLabel(test)} baseline=ESTABLISHED " +
                $"timestampNs={statistics.Baseline.timestampNs} " +
                $"primaryStartDeltaDeg={primaryDeltaFromStationary:F2} " +
                $"crossAxisDeltaDeg={crossAxisDeltaFromStationary:F2}");
        }

        var primaryDeltaFromBaseline = test == ControlledRotationTest.Yaw
            ? SignedTelemetryYawDegrees(statistics.Baseline, current)
            : Mathf.DeltaAngle(statistics.Baseline.gimbalPitchDegrees, current.gimbalPitchDegrees);
        var crossAxisDeltaFromBaseline = test == ControlledRotationTest.Yaw
            ? Mathf.DeltaAngle(statistics.Baseline.gimbalPitchDegrees, current.gimbalPitchDegrees)
            : Mathf.DeltaAngle(statistics.Baseline.aircraftYawDegrees, current.aircraftYawDegrees);
        if (Mathf.Abs(crossAxisDeltaFromBaseline) > controlledTestCrossAxisLimitDegrees)
            return;

        var visualRelative = RelativeVisualRotation(statistics.Baseline, current);
        var telemetryRelative = RelativeTelemetryRotation(statistics.Baseline, current);
        var errorDegrees = Quaternion.Angle(visualRelative, telemetryRelative);
        statistics.Errors.Add(errorDegrees);
        statistics.VisualRotationRangeDegrees = Mathf.Max(
            statistics.VisualRotationRangeDegrees,
            Quaternion.Angle(Quaternion.identity, visualRelative));
        statistics.TelemetryRotationRangeDegrees = Mathf.Max(
            statistics.TelemetryRotationRangeDegrees,
            Quaternion.Angle(Quaternion.identity, telemetryRelative));
        statistics.LastVisualSignedDeltaDegrees = test == ControlledRotationTest.Yaw
            ? SignedVisualYawDegrees(statistics.Baseline, current)
            : SignedRotationAroundTelemetryAxis(visualRelative, telemetryRelative);
        statistics.LastTelemetrySignedDeltaDegrees = primaryDeltaFromBaseline;

        foreach (var offsetMilliseconds in RotationOffsetScanMilliseconds)
        {
            if (!TryGetRelativeTelemetryRotationForOffset(
                    offsetMilliseconds,
                    statistics.Baseline.timestampNs,
                    current.timestampNs,
                    statistics.BaselineTelemetryRotationsByOffset,
                    out var offsetTelemetryRelative))
            {
                continue;
            }

            if (!statistics.OffsetErrors.TryGetValue(offsetMilliseconds, out var offsetStatistics))
            {
                offsetStatistics = new RotationErrorStatistics();
                statistics.OffsetErrors.Add(offsetMilliseconds, offsetStatistics);
            }
            offsetStatistics.Add(Quaternion.Angle(visualRelative, offsetTelemetryRelative));
        }

        if (shouldLogDiagnosticSnapshot && statistics.Errors.Count >= MinimumControlledTestSamples)
            LogControlledRotationTest(statistics);
        if (shouldLogDiagnosticSnapshot)
            LogRotationErrorByHorizon();
    }

    private static Quaternion RelativeVisualRotation(
        PairedRotationDiagnosticSample earlier,
        PairedRotationDiagnosticSample current) =>
        Quaternion.Normalize(current.cameraFromBoardRotation * Quaternion.Inverse(earlier.cameraFromBoardRotation));

    private static Quaternion RelativeTelemetryRotation(
        PairedRotationDiagnosticSample earlier,
        PairedRotationDiagnosticSample current) =>
        Quaternion.Normalize(Quaternion.Inverse(current.navigationFromCameraRotation) * earlier.navigationFromCameraRotation);

    private static float SignedRotationAroundTelemetryAxis(Quaternion visualRotation, Quaternion telemetryRotation)
    {
        visualRotation.ToAngleAxis(out var visualAngle, out var visualAxis);
        telemetryRotation.ToAngleAxis(out _, out var telemetryAxis);
        if (visualAngle > 180f)
        {
            visualAngle = 360f - visualAngle;
            visualAxis = -visualAxis;
        }

        return Vector3.Dot(visualAxis, telemetryAxis) < 0f ? -visualAngle : visualAngle;
    }

    private static float SignedVisualYawDegrees(
        PairedRotationDiagnosticSample earlier,
        PairedRotationDiagnosticSample current) =>
        QuaternionYawValidation.SignedYawDegrees(
            RelativeVisualRotation(earlier, current),
            current.navigationFromCameraRotation);

    private static float SignedTelemetryYawDegrees(
        PairedRotationDiagnosticSample earlier,
        PairedRotationDiagnosticSample current) =>
        QuaternionYawValidation.SignedYawDegrees(
            RelativeTelemetryRotation(earlier, current),
            current.navigationFromCameraRotation);

    private void RecordYawValidationLeg(
        PairedRotationDiagnosticSample current,
        bool shouldLogDiagnosticSnapshot)
    {
        var tracker = _yawLegValidation;
        if (!tracker.HasCenter)
        {
            if (IsYawCenterStable(current))
            {
                tracker.Center = current;
                tracker.HasCenter = true;
                if (shouldLogDiagnosticSnapshot)
                    Debug.Log($"YAW_VALIDATION_CENTER_ESTABLISHED timestampNs={current.timestampNs}");
            }
            return;
        }

        var yawFromCenter = SignedTelemetryYawDegrees(tracker.Center, current);
        var pitchFromCenter = Mathf.Abs(Mathf.DeltaAngle(
            tracker.Center.gimbalPitchDegrees,
            current.gimbalPitchDegrees));
        if (pitchFromCenter > controlledTestCrossAxisLimitDegrees)
        {
            RejectActiveYawLeg("CONTAMINATED_BY_GIMBAL_MOTION", current, pitchFromCenter);
            tracker.Reset();
            return;
        }

        if (tracker.ActiveLeg == null)
        {
            if (Mathf.Abs(yawFromCenter) <= yawCenterToleranceDegrees)
            {
                if (IsYawCenterStable(current))
                    tracker.Center = current;
                return;
            }

            var leg = yawFromCenter < 0f
                ? YawValidationLeg.CenterToLeft
                : YawValidationLeg.CenterToRight;
            BeginYawLeg(leg, tracker.Center, current, yawFromCenter);
            return;
        }

        var active = tracker.ActiveLeg;
        var expectedSign = ExpectedYawSign(active.Leg);
        var directionReversed = expectedSign < 0f
            ? yawFromCenter > tracker.LastTelemetrySignedYawDegrees + stationaryAxisDeltaDegrees
            : yawFromCenter < tracker.LastTelemetrySignedYawDegrees - stationaryAxisDeltaDegrees;
        if (IsOutboundYawLeg(active.Leg) && directionReversed)
        {
            CompleteYawLeg(active, "TURNAROUND");
            var returnLeg = active.Leg == YawValidationLeg.CenterToLeft
                ? YawValidationLeg.LeftToCenter
                : YawValidationLeg.RightToCenter;
            BeginYawLeg(returnLeg, tracker.Peak, current, 0f);
            return;
        }

        AddYawLegSample(active, current);
        tracker.LastTelemetrySignedYawDegrees = yawFromCenter;
        if (Mathf.Abs(yawFromCenter) > Mathf.Abs(tracker.PeakTelemetrySignedYawDegrees))
        {
            tracker.Peak = current;
            tracker.PeakTelemetrySignedYawDegrees = yawFromCenter;
        }

        if (!IsOutboundYawLeg(active.Leg) && Mathf.Abs(yawFromCenter) <= yawCenterToleranceDegrees)
        {
            CompleteYawLeg(active, "CENTER_REACHED");
            tracker.ActiveLeg = null;
            tracker.Center = current;
            tracker.PeakTelemetrySignedYawDegrees = 0f;
            tracker.LastTelemetrySignedYawDegrees = 0f;
        }
    }

    private bool IsYawCenterStable(PairedRotationDiagnosticSample current)
    {
        if (!_hasLastPairedRotationDiagnosticSample)
            return true;

        return Mathf.Abs(SignedTelemetryYawDegrees(_lastPairedRotationDiagnosticSample, current)) <= stationaryAxisDeltaDegrees &&
               Mathf.Abs(Mathf.DeltaAngle(
                   _lastPairedRotationDiagnosticSample.gimbalPitchDegrees,
                   current.gimbalPitchDegrees)) <= stationaryAxisDeltaDegrees;
    }

    private void BeginYawLeg(
        YawValidationLeg leg,
        PairedRotationDiagnosticSample baseline,
        PairedRotationDiagnosticSample current,
        float telemetryYawFromCenter)
    {
        var tracker = _yawLegValidation;
        tracker.ActiveLeg = new YawLegStatistics(leg, baseline);
        tracker.Peak = current;
        tracker.PeakTelemetrySignedYawDegrees = telemetryYawFromCenter;
        tracker.LastTelemetrySignedYawDegrees = telemetryYawFromCenter;
        AddYawLegSample(tracker.ActiveLeg, current);
        Debug.Log($"YAW_VALIDATION_LEG_STARTED label={YawLegLabel(leg)} baselineTimestampNs={baseline.timestampNs}");
    }

    private void AddYawLegSample(YawLegStatistics statistics, PairedRotationDiagnosticSample current)
    {
        var visualRelative = RelativeVisualRotation(statistics.Baseline, current);
        var telemetryRelative = RelativeTelemetryRotation(statistics.Baseline, current);
        var visualSignedYaw = QuaternionYawValidation.SignedYawDegrees(
            visualRelative,
            current.navigationFromCameraRotation);
        var telemetrySignedYaw = QuaternionYawValidation.SignedYawDegrees(
            telemetryRelative,
            current.navigationFromCameraRotation);
        var expectedSign = ExpectedYawSign(statistics.Leg);
        if (Mathf.Abs(visualSignedYaw) > yawCenterToleranceDegrees &&
            Mathf.Abs(telemetrySignedYaw) > yawCenterToleranceDegrees &&
            (Mathf.Sign(visualSignedYaw) != expectedSign || Mathf.Sign(telemetrySignedYaw) != expectedSign))
        {
            statistics.SignedDirectionMismatches++;
        }

        statistics.PairedSamples++;
        statistics.QuaternionErrors.Add(Quaternion.Angle(visualRelative, telemetryRelative));
        statistics.MaximumGimbalPitchChangeDegrees = Mathf.Max(
            statistics.MaximumGimbalPitchChangeDegrees,
            Mathf.Abs(Mathf.DeltaAngle(statistics.Baseline.gimbalPitchDegrees, current.gimbalPitchDegrees)));
        statistics.LastVisualSignedYawDegrees = visualSignedYaw;
        statistics.LastTelemetrySignedYawDegrees = telemetrySignedYaw;
    }

    private void RejectActiveYawLeg(
        string reason,
        PairedRotationDiagnosticSample current,
        float pitchChangeDegrees)
    {
        if (_yawLegValidation.ActiveLeg == null)
            return;

        Debug.Log(
            $"YAW_VALIDATION_LEG_REJECTED label={YawLegLabel(_yawLegValidation.ActiveLeg.Leg)} " +
            $"reason={reason} timestampNs={current.timestampNs} gimbalPitchChangeDeg={pitchChangeDegrees:F2}");
    }

    private void CompleteYawLeg(YawLegStatistics statistics, string completion)
    {
        var valid = statistics.PairedSamples >= minimumYawLegPairedSamples &&
                    statistics.MaximumGimbalPitchChangeDegrees <= controlledTestCrossAxisLimitDegrees &&
                    statistics.SignedDirectionMismatches == 0;
        var reason = statistics.PairedSamples < minimumYawLegPairedSamples
            ? "INSUFFICIENT_PAIRED_SAMPLES"
            : statistics.MaximumGimbalPitchChangeDegrees > controlledTestCrossAxisLimitDegrees
                ? "CONTAMINATED_BY_GIMBAL_MOTION"
                : statistics.SignedDirectionMismatches > 0
                    ? "SIGNED_DIRECTION_MISMATCH"
                    : "VALID";
        Debug.Log(
            $"YAW_VALIDATION_LEG label={YawLegLabel(statistics.Leg)} completion={completion} valid={valid} reason={reason} " +
            $"pairedSamples={statistics.PairedSamples} visualSignedYawDeg={statistics.LastVisualSignedYawDegrees:F2} " +
            $"telemetrySignedYawDeg={statistics.LastTelemetrySignedYawDegrees:F2} " +
            $"directionMismatches={statistics.SignedDirectionMismatches} " +
            $"gimbalPitchChangeDeg={statistics.MaximumGimbalPitchChangeDegrees:F2} " +
            $"meanQuaternionErrorDeg={statistics.QuaternionErrors.MeanDegrees:F2} " +
            $"medianQuaternionErrorDeg={statistics.QuaternionErrors.MedianDegrees:F2} " +
            $"p95QuaternionErrorDeg={statistics.QuaternionErrors.P95Degrees:F2}");
    }

    private static bool IsOutboundYawLeg(YawValidationLeg leg) =>
        leg == YawValidationLeg.CenterToLeft || leg == YawValidationLeg.CenterToRight;

    private static float ExpectedYawSign(YawValidationLeg leg) =>
        leg == YawValidationLeg.CenterToLeft || leg == YawValidationLeg.RightToCenter ? -1f : 1f;

    private static string YawLegLabel(YawValidationLeg leg) => leg switch
    {
        YawValidationLeg.CenterToLeft => "CENTER_TO_LEFT",
        YawValidationLeg.LeftToCenter => "LEFT_TO_CENTER",
        YawValidationLeg.CenterToRight => "CENTER_TO_RIGHT",
        YawValidationLeg.RightToCenter => "RIGHT_TO_CENTER",
        _ => "NONE"
    };

    private void LogControlledRotationTest(ControlledRotationTestStatistics statistics)
    {
        var label = ControlledTestLabel(statistics.Test);
        var result = ClassifyControlledRotationTest(statistics, out var bestOffsetMilliseconds);
        var offsetLines = new List<string>();
        foreach (var offsetMilliseconds in RotationOffsetScanMilliseconds)
        {
            if (!statistics.OffsetErrors.TryGetValue(offsetMilliseconds, out var offsetStatistics) || offsetStatistics.Count == 0)
                continue;
            offsetLines.Add(
                $"offsetMs={offsetMilliseconds} sampleCount={offsetStatistics.Count} " +
                $"mean={offsetStatistics.MeanDegrees:F2} median={offsetStatistics.MedianDegrees:F2} " +
                $"p95={offsetStatistics.P95Degrees:F2}");
        }
        Debug.Log(
            $"{label} pairedSamples={statistics.Errors.Count} " +
            $"visualTotalRotationRangeDeg={statistics.VisualRotationRangeDegrees:F2} " +
            $"telemetryTotalRotationRangeDeg={statistics.TelemetryRotationRangeDegrees:F2} " +
            $"meanRotationErrorDeg={statistics.Errors.MeanDegrees:F2} " +
            $"medianRotationErrorDeg={statistics.Errors.MedianDegrees:F2} " +
            $"p95RotationErrorDeg={statistics.Errors.P95Degrees:F2} " +
            $"maxRotationErrorDeg={statistics.Errors.MaxDegrees:F2} " +
            $"visual{(statistics.Test == ControlledRotationTest.Yaw ? "Yaw" : "Pitch")}DeltaDeg={statistics.LastVisualSignedDeltaDegrees:F2} " +
            $"telemetry{(statistics.Test == ControlledRotationTest.Yaw ? "Yaw" : "Pitch")}DeltaDeg={statistics.LastTelemetrySignedDeltaDegrees:F2} " +
            $"bestDiagnosticOffsetMs={bestOffsetMilliseconds} {label}_RESULT={result} " +
            $"OFFSET_SCAN=[{string.Join(" | ", offsetLines)}]");
    }

    private void LogRotationErrorByHorizon()
    {
        var lines = new List<string>();
        foreach (var horizonMilliseconds in RotationErrorHorizonsMilliseconds)
        {
            if (!_rotationErrorByHorizonStatistics.TryGetValue(horizonMilliseconds, out var statistics) || statistics.Count == 0)
                continue;
            lines.Add(
                $"{horizonMilliseconds}ms sampleCount={statistics.Count} mean={statistics.MeanDegrees:F2} " +
                $"median={statistics.MedianDegrees:F2} p95={statistics.P95Degrees:F2}");
        }

        if (lines.Count > 0)
            Debug.Log($"ROTATION_ERROR_BY_HORIZON {string.Join(" | ", lines)}");
    }

    private static string ControlledTestLabel(ControlledRotationTest test) =>
        test == ControlledRotationTest.Yaw ? "YAW_TEST" : "GIMBAL_PITCH_TEST";

    private bool TryGetRelativeTelemetryRotationForOffset(
        int offsetMilliseconds,
        long currentVisionTimestampNs,
        out Quaternion telemetryCamera1FromCamera0)
    {
        return TryGetRelativeTelemetryRotationForOffset(
            offsetMilliseconds,
            _rotationComparisonBaseline.visionTimestampNs,
            currentVisionTimestampNs,
            _baselineTelemetryRotationsByOffset,
            out telemetryCamera1FromCamera0);
    }

    private bool TryGetRelativeTelemetryRotationForOffset(
        int offsetMilliseconds,
        long baselineVisionTimestampNs,
        long currentVisionTimestampNs,
        Dictionary<int, Quaternion> baselineTelemetryRotationsByOffset,
        out Quaternion telemetryCamera1FromCamera0)
    {
        telemetryCamera1FromCamera0 = Quaternion.identity;
        var offsetNs = offsetMilliseconds * 1_000_000L;
        if (!baselineTelemetryRotationsByOffset.TryGetValue(offsetMilliseconds, out var baselineRotation))
        {
            if (!TryInterpolateTelemetryRotation(
                    baselineVisionTimestampNs + offsetNs,
                    out var baselineTelemetry,
                    out _))
            {
                return false;
            }
            baselineRotation = baselineTelemetry.navigationFromCameraRotation;
            baselineTelemetryRotationsByOffset.Add(offsetMilliseconds, baselineRotation);
        }
        if (!TryInterpolateTelemetryRotation(currentVisionTimestampNs + offsetNs, out var currentTelemetry, out _))
        {
            return false;
        }

        telemetryCamera1FromCamera0 = Quaternion.Normalize(
            Quaternion.Inverse(currentTelemetry.navigationFromCameraRotation) *
            baselineRotation);
        return true;
    }

    private bool TryInterpolateTelemetryRotation(
        long targetTimestampNs,
        out InterpolatedTelemetryRotation interpolated,
        out string reason,
        string diagnosticPurpose = "ROTATION_COMPARISON")
    {
        interpolated = default;
        reason = string.Empty;
        DjiCameraPoseProvider.TelemetryOrientationSample? before = null;
        DjiCameraPoseProvider.TelemetryOrientationSample? after = null;
        var diagnosticAlpha = 0f;
        var diagnosticSelected = default(Quaternion);
        var diagnosticValid = false;
        try
        {
        if (_telemetryProvider == null || !_telemetryProvider.HasTelemetryOrientation)
        {
            reason = "TELEMETRY_ORIENTATION_UNAVAILABLE";
            return false;
        }

        foreach (var sample in _telemetryProvider.TelemetryOrientationHistory)
        {
            if (!sample.hasOrientation)
                continue;
            if (sample.timestampNs <= targetTimestampNs)
                before = sample;
            if (sample.timestampNs >= targetTimestampNs)
            {
                after = sample;
                break;
            }
        }

        if (!before.HasValue || !after.HasValue)
        {
            reason = "TELEMETRY_BRACKET_UNAVAILABLE";
            return false;
        }

        var maximumBracketAgeNs = maximumTelemetryBracketAgeMilliseconds * 1_000_000.0;
        var beforeDeltaNs = targetTimestampNs - before.Value.timestampNs;
        var afterDeltaNs = after.Value.timestampNs - targetTimestampNs;
        if (beforeDeltaNs < 0L || afterDeltaNs < 0L ||
            beforeDeltaNs > maximumBracketAgeNs || afterDeltaNs > maximumBracketAgeNs)
        {
            reason = $"TELEMETRY_BRACKET_TOO_OLD beforeDeltaMs={beforeDeltaNs / 1_000_000.0:F2} afterDeltaMs={afterDeltaNs / 1_000_000.0:F2}";
            return false;
        }

        var spanNs = after.Value.timestampNs - before.Value.timestampNs;
        var alpha = spanNs == 0L
            ? 0f
            : Mathf.Clamp01((float)((targetTimestampNs - before.Value.timestampNs) / (double)spanNs));
        interpolated = new InterpolatedTelemetryRotation(
            Quaternion.Slerp(
                before.Value.navigationFromCameraRotation,
                after.Value.navigationFromCameraRotation,
                alpha),
            before.Value.timestampNs,
            after.Value.timestampNs,
            alpha,
            (float)(Math.Min(beforeDeltaNs, afterDeltaNs) / 1_000_000.0),
            Mathf.LerpAngle(before.Value.aircraftYawDegrees, after.Value.aircraftYawDegrees, alpha),
            Mathf.LerpAngle(before.Value.gimbalPitchDegrees, after.Value.gimbalPitchDegrees, alpha));
        diagnosticAlpha = alpha;
        diagnosticSelected = interpolated.navigationFromCameraRotation;
        diagnosticValid = true;
        return true;
        }
        finally
        {
            if (diagnosticPurpose != "ROTATION_COMPARISON")
                TraceOrientationBracket(targetTimestampNs, diagnosticPurpose, before, after,
                    diagnosticAlpha, diagnosticSelected, diagnosticValid, reason);
        }
    }

    private void LogRotationOffsetScan()
    {
        if (!_rotationOffsetStatistics.TryGetValue(0, out var zeroOffset) ||
            zeroOffset.Count < MinimumOffsetScanSamplesForClassification)
        {
            Debug.Log($"ROTATION_TIME_OFFSET_SCAN result=INSUFFICIENT_DATA zeroOffsetSamples={zeroOffset?.Count ?? 0}");
            return;
        }

        var lines = new List<string>();
        var bestOffsetMilliseconds = 0;
        var bestMeanErrorDegrees = float.PositiveInfinity;
        foreach (var offsetMilliseconds in RotationOffsetScanMilliseconds)
        {
            if (!_rotationOffsetStatistics.TryGetValue(offsetMilliseconds, out var statistics) || statistics.Count == 0)
                continue;
            lines.Add(
                $"offsetMs={offsetMilliseconds} sampleCount={statistics.Count} " +
                $"meanRotationErrorDeg={statistics.MeanDegrees:F2} " +
                $"medianRotationErrorDeg={statistics.MedianDegrees:F2} " +
                $"p95RotationErrorDeg={statistics.P95Degrees:F2}");
            if (statistics.MeanDegrees < bestMeanErrorDegrees)
            {
                bestMeanErrorDegrees = statistics.MeanDegrees;
                bestOffsetMilliseconds = offsetMilliseconds;
            }
        }

        var result = ClassifyRotationComparison(zeroOffset.MeanDegrees, bestOffsetMilliseconds, bestMeanErrorDegrees);
        Debug.Log(
            $"ROTATION_TIME_OFFSET_SCAN {string.Join(" | ", lines)} bestDiagnosticOffsetMs={bestOffsetMilliseconds} " +
            $"bestMeanErrorDeg={bestMeanErrorDegrees:F2} errorAt0MsDeg={zeroOffset.MeanDegrees:F2} " +
            $"VISUAL_TELEMETRY_ROTATION_RESULT={result}");
    }

    private static string ClassifyRotationComparison(float zeroOffsetMeanError, int bestOffsetMilliseconds, float bestMeanError)
    {
        // A mixed aircraft/gimbal interval is useful for transport validation,
        // but cannot establish either axis convention or propagation safety.
        return "MIXED_MOTION_INCONCLUSIVE";
    }

    private string ClassifyControlledRotationTest(
        ControlledRotationTestStatistics statistics,
        out int bestOffsetMilliseconds)
    {
        bestOffsetMilliseconds = 0;
        if (statistics.Errors.Count < MinimumControlledTestSamples)
            return "INSUFFICIENT_DATA";

        var bestMeanError = float.PositiveInfinity;
        foreach (var offsetMilliseconds in RotationOffsetScanMilliseconds)
        {
            if (!statistics.OffsetErrors.TryGetValue(offsetMilliseconds, out var offsetStatistics) ||
                offsetStatistics.Count < MinimumControlledTestSamples)
            {
                continue;
            }

            if (offsetStatistics.MeanDegrees < bestMeanError)
            {
                bestMeanError = offsetStatistics.MeanDegrees;
                bestOffsetMilliseconds = offsetMilliseconds;
            }
        }

        if (bestMeanError >= 20f || statistics.Errors.MeanDegrees >= 20f)
            return "CONVENTION_MISMATCH";

        if (bestOffsetMilliseconds != 0 &&
            bestMeanError + 2f < statistics.Errors.MeanDegrees &&
            bestMeanError < statistics.Errors.MeanDegrees * 0.7f)
        {
            return "TIMING_OFFSET";
        }

        if (_rotationErrorByHorizonStatistics.TryGetValue(250, out var at250Milliseconds) &&
            _rotationErrorByHorizonStatistics.TryGetValue(2000, out var at2000Milliseconds) &&
            at250Milliseconds.Count >= MinimumControlledTestSamples &&
            at2000Milliseconds.Count >= MinimumControlledTestSamples &&
            at2000Milliseconds.MeanDegrees > at250Milliseconds.MeanDegrees * 2f + 2f)
        {
            return "DRIFT_WITH_DURATION";
        }

        return "GOOD";
    }

    private void ResetRotationComparison(string reason)
    {
        if (!_hasRotationComparisonBaseline &&
            _pendingRotationDiagnosticSamples.Count == 0 &&
            _pairedRotationDiagnosticSamples.Count == 0)
            return;

        _hasRotationComparisonBaseline = false;
        _rotationOffsetStatistics.Clear();
        _rotationErrorByHorizonStatistics.Clear();
        _baselineTelemetryRotationsByOffset.Clear();
        _pendingRotationDiagnosticSamples.Clear();
        _pairedRotationDiagnosticSamples.Clear();
        _controlledRotationTestStatistics.Clear();
        _yawLegValidation.Reset();
        _hasLastPairedRotationDiagnosticSample = false;
        Debug.Log($"DJI_ROTATION_SYNC baseline=RESET reason={reason}");
    }

    private void LogRotationConventionSanityCheck()
    {
        if (_rotationConventionSanityLogged)
            return;

        _rotationConventionSanityLogged = true;
        var navigationFromBoard = Quaternion.AngleAxis(29f, new Vector3(0.2f, 0.8f, 0.5f).normalized);
        var camera0FromBoard = Quaternion.AngleAxis(-17f, Vector3.right);
        var camera1FromBoard = Quaternion.AngleAxis(31f, Vector3.up) * camera0FromBoard;
        var navigationFromCamera0 = navigationFromBoard * Quaternion.Inverse(camera0FromBoard);
        var navigationFromCamera1 = navigationFromBoard * Quaternion.Inverse(camera1FromBoard);
        var visualCamera1FromCamera0 = camera1FromBoard * Quaternion.Inverse(camera0FromBoard);
        var telemetryCamera1FromCamera0 = Quaternion.Inverse(navigationFromCamera1) * navigationFromCamera0;
        var errorDegrees = Quaternion.Angle(visualCamera1FromCamera0, telemetryCamera1FromCamera0);
        var syntheticPositiveYaw = QuaternionYawValidation.SignedTwistDegrees(
            Quaternion.AngleAxis(10f, QuaternionYawValidation.NavigationYawAxis),
            QuaternionYawValidation.NavigationYawAxis);
        var syntheticNegativeYaw = QuaternionYawValidation.SignedTwistDegrees(
            Quaternion.AngleAxis(-10f, QuaternionYawValidation.NavigationYawAxis),
            QuaternionYawValidation.NavigationYawAxis);
        Debug.Log(
            $"DJI_ROTATION_CONVENTION_SANITY visual={FormatRotation(visualCamera1FromCamera0)} " +
            $"telemetry={FormatRotation(telemetryCamera1FromCamera0)} errorDeg={errorDegrees:F5} expectedMaxDeg=0.01");
        Debug.Log(
            "YAW_VALIDATION_CONVENTION axis=NED_PLUS_Z_DOWN_EXPRESSED_IN_CURRENT_CAMERA " +
            "positiveYaw=RIGHT_HAND_ABOUT_NED_PLUS_Z_DOWN_CLOCKWISE_FROM_ABOVE " +
            "relativeRotation=C1_FROM_C0 " +
            $"syntheticPlus10Deg={syntheticPositiveYaw:F2} syntheticMinus10Deg={syntheticNegativeYaw:F2}");
    }

    private static long GetVisionTimestamp(NativeBoardResult result) =>
        result.visionFrameTimestampNs != 0L ? result.visionFrameTimestampNs : result.timestampNs;

    private static string FormatRotation(Quaternion rotation)
    {
        rotation.ToAngleAxis(out var angleDegrees, out var axis);
        if (angleDegrees > 180f)
        {
            angleDegrees = 360f - angleDegrees;
            axis = -axis;
        }
        return $"angleDeg={angleDegrees:F2} axis=({axis.x:F3},{axis.y:F3},{axis.z:F3})";
    }

    private void LogStationaryJitter()
    {
        if (_stationaryPositionDeltas.Count == 0)
            return;

        Debug.Log($"DJI_VISUAL_STATIONARY_JITTER samples={_stationaryPositionDeltas.Count} positionRmsMeters={Rms(_stationaryPositionDeltas):F4} rotationRmsDegrees={Rms(_stationaryRotationDeltas):F3}");
    }

    private void RecordProvisionalDiagnostics(NativeBoardResult result, Pose cameraFromBoard, bool shouldLogDiagnosticSnapshot)
    {
        if (!_calibration.Current.provisional || !_calibration.AllowProvisionalCalibrationForValidation)
            return;

        _provisionalRawAcceptedSampleCount++;
        _provisionalReprojectionRms.Add(result.reprojectionRms);
        _provisionalMaxResiduals.Add(result.maxResidual);

        var markerCenter = GetMarkerImageCenter(result);
        var boardNormalInCamera = cameraFromBoard.rotation * Vector3.forward;
        var distance = cameraFromBoard.position.magnitude;
        var activeBuckets = GetActiveValidationBuckets(markerCenter, result, boardNormalInCamera, distance);
        var activeBucketFlags = new bool[_validationBucketStatistics.Length];
        foreach (var bucket in activeBuckets)
            activeBucketFlags[(int)bucket] = true;
        for (var index = 0; index < _validationBucketStatistics.Length; index++)
        {
            var statistics = _validationBucketStatistics[index];
            if (activeBucketFlags[index])
            {
                statistics.Observe(
                    result,
                    cameraFromBoard,
                    stationarySettleSamples,
                    stationaryFramePositionDeltaMeters,
                    stationaryFrameRotationDeltaDegrees);
            }
            else
            {
                // A bucket transition must never contribute camera motion to
                // the stationary jitter statistics of the previous bucket.
                statistics.MarkInactive();
            }
        }
        _calibration.RegisterPhysicalValidationObservation(TotalStationaryAcceptedSamples(), HasCompleteValidationCoverage());

        if (!shouldLogDiagnosticSnapshot)
            return;

        Debug.Log(
            $"DJI_PROVISIONAL_CALIBRATION_STATS rawAcceptedSamples={_provisionalRawAcceptedSampleCount} " +
            $"meanRmsPixels={Mean(_provisionalReprojectionRms):F3} medianRmsPixels={Median(_provisionalReprojectionRms):F3} " +
            $"maxRmsPixels={Max(_provisionalReprojectionRms):F3} maxCornerResidualPixels={Max(_provisionalMaxResiduals):F3} " +
            $"stationaryAcceptedSamples={TotalStationaryAcceptedSamples()}");
        Debug.Log(
            $"DJI_BOARD_VALIDATION_BUCKET current={string.Join(",", Array.ConvertAll(activeBuckets, bucket => bucket.ToString()))} " +
            $"distanceMeters={distance:F3} stationaryPositionThresholdMeters={stationaryFramePositionDeltaMeters:F4} " +
            $"stationaryRotationThresholdDegrees={stationaryFrameRotationDeltaDegrees:F2} settleSamples={stationarySettleSamples}");
        foreach (var statistics in _validationBucketStatistics)
            Debug.Log(statistics.ToDiagnosticString());
        Debug.Log(
            $"DJI_EDGE_RMS_COMPARISON centerMeanRmsPixels={_validationBucketStatistics[(int)ValidationBucket.CENTER].MeanRmsPixels:F3} " +
            $"leftMeanRmsPixels={_validationBucketStatistics[(int)ValidationBucket.LEFT].MeanRmsPixels:F3} " +
            $"rightMeanRmsPixels={_validationBucketStatistics[(int)ValidationBucket.RIGHT].MeanRmsPixels:F3} " +
            $"topMeanRmsPixels={_validationBucketStatistics[(int)ValidationBucket.TOP].MeanRmsPixels:F3} " +
            $"bottomMeanRmsPixels={_validationBucketStatistics[(int)ValidationBucket.BOTTOM].MeanRmsPixels:F3}");
        Debug.Log(
            $"DJI_PROVISIONAL_VALIDATION_COVERAGE stationaryAcceptedSamples={TotalStationaryAcceptedSamples()} " +
            $"minimumSamplesPerBucket={minimumSamplesPerValidationBucket} complete={HasCompleteValidationCoverage()} " +
            $"state={_calibration.CurrentValidationState} automaticClassification=DISABLED");
    }

    private static Vector2 GetMarkerImageCenter(NativeBoardResult result)
    {
        if (result.markers == null || result.markers.Length == 0)
            return new Vector2(result.frameWidth * 0.5f, result.frameHeight * 0.5f);

        var sum = Vector2.zero;
        var count = 0;
        foreach (var marker in result.markers)
        {
            if (marker.detectedCorners == null)
                continue;
            foreach (var corner in marker.detectedCorners)
            {
                if (corner?.Length < 2)
                    continue;
                sum += new Vector2(corner[0], corner[1]);
                count++;
            }
        }

        if (count == 0 || result.frameWidth <= 0 || result.frameHeight <= 0)
            return new Vector2(result.frameWidth * 0.5f, result.frameHeight * 0.5f);
        return sum / count;
    }

    private ValidationBucket[] GetActiveValidationBuckets(Vector2 center, NativeBoardResult result, Vector3 boardNormalInCamera, float distanceMeters)
    {
        var normalized = new Vector2(center.x / result.frameWidth, center.y / result.frameHeight);
        var buckets = new List<ValidationBucket>(4)
        {
            ClassifyValidationLocation(normalized)
        };

        var moderateTiltThreshold = Mathf.Sin(moderateTiltDegrees * Mathf.Deg2Rad);
        // A rotation about camera X changes the normal's camera-Y component,
        // while a rotation about camera Y changes its camera-X component.
        if (Mathf.Abs(boardNormalInCamera.y) >= moderateTiltThreshold)
            buckets.Add(ValidationBucket.X_TILT);
        if (Mathf.Abs(boardNormalInCamera.x) >= moderateTiltThreshold)
            buckets.Add(ValidationBucket.Y_TILT);
        if (distanceMeters <= nearValidationDistanceMeters)
            buckets.Add(ValidationBucket.NEAR);
        if (distanceMeters >= farValidationDistanceMeters)
            buckets.Add(ValidationBucket.FAR);
        return buckets.ToArray();
    }

    private static ValidationBucket ClassifyValidationLocation(Vector2 normalized)
    {
        if (normalized.x <= 1f / 3f)
            return ValidationBucket.LEFT;
        if (normalized.x >= 2f / 3f)
            return ValidationBucket.RIGHT;
        if (normalized.y <= 1f / 3f)
            return ValidationBucket.TOP;
        if (normalized.y >= 2f / 3f)
            return ValidationBucket.BOTTOM;
        return ValidationBucket.CENTER;
    }

    private bool HasCompleteValidationCoverage()
    {
        foreach (var statistics in _validationBucketStatistics)
        {
            if (statistics.AcceptedSamples < minimumSamplesPerValidationBucket)
                return false;
        }

        return true;
    }

    private int TotalStationaryAcceptedSamples()
    {
        var total = 0;
        foreach (var statistics in _validationBucketStatistics)
            total += statistics.AcceptedSamples;
        return total;
    }

    private bool ShouldLogDiagnosticSnapshot()
    {
        if (!diagnosticLogging || Time.unscaledTime < _nextDiagnosticLogTime)
            return false;
        _nextDiagnosticLogTime = Time.unscaledTime + diagnosticLogIntervalSeconds;
        return true;
    }

    private static float[] BuildNativeMarkerLayout(IReadOnlyList<ReferenceBoardDefinition.FiducialMarkerDefinition> markers)
    {
        var data = new float[markers.Count * 9];
        for (var index = 0; index < markers.Count; index++)
        {
            var marker = markers[index];
            var offset = index * 9;
            var rotation = marker.RotationInBoard;
            data[offset] = int.Parse(marker.Id);
            data[offset + 1] = marker.AprilTagDetectionSizeMeters;
            data[offset + 2] = marker.CenterInBoard.x;
            data[offset + 3] = marker.CenterInBoard.y;
            data[offset + 4] = marker.CenterInBoard.z;
            data[offset + 5] = rotation.x;
            data[offset + 6] = rotation.y;
            data[offset + 7] = rotation.z;
            data[offset + 8] = rotation.w;
        }
        return data;
    }

    private static float Rms(IReadOnlyList<float> values)
    {
        var sum = 0f;
        foreach (var value in values)
            sum += value * value;
        return Mathf.Sqrt(sum / values.Count);
    }

    private static float RmsOrZero(IReadOnlyList<float> values) => values.Count == 0 ? 0f : Rms(values);

    private static float Mean(IReadOnlyList<float> values)
    {
        if (values.Count == 0)
            return 0f;
        var sum = 0f;
        foreach (var value in values)
            sum += value;
        return sum / values.Count;
    }

    private static float Mean(IReadOnlyList<int> values)
    {
        if (values.Count == 0)
            return 0f;
        var sum = 0f;
        foreach (var value in values)
            sum += value;
        return sum / values.Count;
    }

    private static int MinOrZero(IReadOnlyList<int> values)
    {
        if (values.Count == 0)
            return 0;
        var minimum = int.MaxValue;
        foreach (var value in values)
            minimum = Mathf.Min(minimum, value);
        return minimum;
    }

    private static float Median(IReadOnlyList<float> values)
    {
        if (values.Count == 0)
            return 0f;
        var copy = new List<float>(values);
        copy.Sort();
        var middle = copy.Count / 2;
        return copy.Count % 2 == 0 ? (copy[middle - 1] + copy[middle]) * 0.5f : copy[middle];
    }

    private static float Max(IReadOnlyList<float> values)
    {
        var maximum = 0f;
        foreach (var value in values)
            maximum = Mathf.Max(maximum, value);
        return maximum;
    }

    private static float Percentile(IReadOnlyList<float> values, float percentile)
    {
        if (values.Count == 0)
            return 0f;
        var sorted = new List<float>(values);
        sorted.Sort();
        var index = Mathf.Clamp(Mathf.CeilToInt(percentile * sorted.Count) - 1, 0, sorted.Count - 1);
        return sorted[index];
    }

    private static float PositionStandardDeviation(IReadOnlyList<Vector3> positions)
    {
        if (positions.Count == 0)
            return 0f;
        var mean = Vector3.zero;
        foreach (var position in positions)
            mean += position;
        mean /= positions.Count;

        var sumSquaredDistance = 0f;
        foreach (var position in positions)
            sumSquaredDistance += (position - mean).sqrMagnitude;
        return Mathf.Sqrt(sumSquaredDistance / positions.Count);
    }

    private static float RotationStandardDeviation(IReadOnlyList<Quaternion> rotations)
    {
        if (rotations.Count == 0)
            return 0f;

        var reference = rotations[0];
        var sum = Vector4.zero;
        foreach (var rotation in rotations)
        {
            // Quaternions q and -q are identical orientations; align signs
            // before calculating their arithmetic mean.
            var aligned = Quaternion.Dot(reference, rotation) < 0f
                ? new Quaternion(-rotation.x, -rotation.y, -rotation.z, -rotation.w)
                : rotation;
            sum += new Vector4(aligned.x, aligned.y, aligned.z, aligned.w);
        }

        if (sum.sqrMagnitude <= Mathf.Epsilon)
            return 0f;
        var mean = Quaternion.Normalize(new Quaternion(sum.x, sum.y, sum.z, sum.w));
        var sumSquaredAngles = 0f;
        foreach (var rotation in rotations)
        {
            var angle = Quaternion.Angle(mean, rotation);
            sumSquaredAngles += angle * angle;
        }
        return Mathf.Sqrt(sumSquaredAngles / rotations.Count);
    }

    private void DrawBoardAxes(Pose cameraFromBoard, NativeBoardResult result)
    {
        const float axisLengthMeters = 0.08f;
        var origin = ProjectBoardPoint(Vector3.zero, cameraFromBoard);
        DrawPixelLine(origin, ProjectBoardPoint(Vector3.right * axisLengthMeters, cameraFromBoard), result, Color.red);
        DrawPixelLine(origin, ProjectBoardPoint(Vector3.up * axisLengthMeters, cameraFromBoard), result, Color.green);
        DrawPixelLine(origin, ProjectBoardPoint(Vector3.forward * axisLengthMeters, cameraFromBoard), result, Color.blue);
    }

    private Vector2 ProjectBoardPoint(Vector3 boardPoint, Pose cameraFromBoard)
    {
        var cameraPoint = cameraFromBoard.rotation * boardPoint + cameraFromBoard.position;
        if (cameraPoint.z <= 0.0001f)
            return new Vector2(float.NaN, float.NaN);
        var data = _calibration.Current;
        var x = cameraPoint.x / cameraPoint.z;
        var y = cameraPoint.y / cameraPoint.z;
        var r2 = x * x + y * y;
        var radial = 1f + data.k1 * r2 + data.k2 * r2 * r2 + data.k3 * r2 * r2 * r2;
        var distortedX = x * radial + 2f * data.p1 * x * y + data.p2 * (r2 + 2f * x * x);
        var distortedY = y * radial + data.p1 * (r2 + 2f * y * y) + 2f * data.p2 * x * y;
        return new Vector2(data.fx * distortedX + data.cx, data.fy * distortedY + data.cy);
    }

    private void DrawPixelQuad(float[][] corners, NativeBoardResult result, Color color)
    {
        if (corners == null || corners.Length != 4)
            return;
        for (var index = 0; index < 4; index++)
        {
            var next = (index + 1) % 4;
            if (corners[index]?.Length < 2 || corners[next]?.Length < 2)
                return;
            DrawPixelLine(new Vector2(corners[index][0], corners[index][1]), new Vector2(corners[next][0], corners[next][1]), result, color);
        }
    }

    private void DrawPixelLine(Vector2 first, Vector2 second, NativeBoardResult result, Color color)
    {
        if (!float.IsFinite(first.x) || !float.IsFinite(first.y) || !float.IsFinite(second.x) || !float.IsFinite(second.y))
            return;
        var a = new Vector2(first.x / result.frameWidth * Screen.width, first.y / result.frameHeight * Screen.height);
        var b = new Vector2(second.x / result.frameWidth * Screen.width, second.y / result.frameHeight * Screen.height);
        var length = Vector2.Distance(a, b);
        if (length < 1f)
            return;
        _debugLineTexture ??= CreateDebugLineTexture();
        var previousColor = GUI.color;
        var previousMatrix = GUI.matrix;
        GUI.color = color;
        GUIUtility.RotateAroundPivot(Vector2.SignedAngle(Vector2.right, b - a), a);
        GUI.DrawTexture(new Rect(a.x, a.y - 1.5f, length, 3f), _debugLineTexture);
        GUI.matrix = previousMatrix;
        GUI.color = previousColor;
    }

    private static Texture2D CreateDebugLineTexture()
    {
        var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        texture.SetPixel(0, 0, Color.white);
        texture.Apply();
        return texture;
    }

    /// <summary>Starts a bounded raw-luma capture from the actual detector path.</summary>
    public string RequestCalibrationCapture(int frameCount = 30)
    {
        var path = DjiBoardVisionBridge.RequestCalibrationCapture(frameCount);
        if (string.IsNullOrWhiteSpace(path))
            Debug.LogWarning("DJI_CALIBRATION_CAPTURE_REQUEST_FAILED");
        else
            Debug.Log($"DJI_CALIBRATION_CAPTURE_REQUESTED frames={frameCount} path={path}");
        return path;
    }

    public void SetAllowProvisionalCalibrationForValidation(bool allowed)
    {
        allowProvisionalCalibrationForValidation = allowed;
        _calibration.SetAllowProvisionalCalibrationForValidation(allowed);
    }

    public bool TrySetProvisionalValidationResult(DjiCameraCalibration.ValidationState result, string evidenceSummary)
    {
        return _calibration.TrySetProvisionalValidationResult(result, evidenceSummary);
    }

    public void ResetProvisionalValidation()
    {
        _calibration.ResetProvisionalValidation();
    }
}

internal static class DjiBoardVisionBridge
{
#if UNITY_ANDROID && !UNITY_EDITOR
    private const string BridgeClass = "com.sok9hu.djibridge.DJIUnityVideoBridge";
    private const string VisionClass = "com.sok9hu.djibridge.DjiBoardVisionBridge";
#endif

    public static void Configure(int width, int height, float fx, float fy, float cx, float cy, float[] distortionCoefficients, float[] layout, int intervalMs)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        using var bridge = new AndroidJavaClass(VisionClass);
        bridge.CallStatic("configure", width, height, fx, fy, cx, cy, distortionCoefficients, layout, intervalMs);
#endif
    }

    public static bool Start()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        using var bridge = new AndroidJavaClass(BridgeClass);
        return bridge.CallStatic<bool>("startBoardVision");
#else
        return false;
#endif
    }

    public static void Stop()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        using var bridge = new AndroidJavaClass(BridgeClass);
        bridge.CallStatic("stopBoardVision");
#endif
    }

    public static string GetLatestResultJson()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        using var bridge = new AndroidJavaClass(BridgeClass);
        return bridge.CallStatic<string>("getLatestBoardVisionJson");
#else
        return string.Empty;
#endif
    }

    public static long GetMonotonicTimestampNs()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        using var systemClock = new AndroidJavaClass("android.os.SystemClock");
        return systemClock.CallStatic<long>("elapsedRealtimeNanos");
#else
        return 0L;
#endif
    }

    public static string RequestCalibrationCapture(int frameCount)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        using var bridge = new AndroidJavaClass(BridgeClass);
        return bridge.CallStatic<string>("requestBoardCalibrationCapture", frameCount);
#else
        return string.Empty;
#endif
    }
}
