using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Coordinates authoritative visual measurements for the persistent-world
/// localization path. It never chooses render authority.
/// </summary>
public sealed class DjiWorldLocalizationShadowCoordinator
{
    private const float TransformTolerance = 0.0001f;
    // 32 visual frames covers the observed 1-2 frame telemetry-bracketing lag
    // while remaining bounded and scoped to one DroneView localization session.
    private const int AuthoritativeVisualHistoryCapacity = 32;
    private readonly WorldLocalizationState _state = new();
    private readonly WorldRotationPropagationEquivalenceDiagnostics _rotationPropagationEquivalence = new();
    private readonly HorizontalGnssSmoothingEstimator _horizontalGnssSmoothingEstimator = new();
    private readonly HorizontalGnssQualityAdmissionGate _horizontalGnssQualityAdmissionGate = new();
    private readonly VerticalAltitudeSmoothingEstimator _verticalAltitudeSmoothingEstimator = new();
    private readonly List<AuthoritativeWorldVisualSnapshot> _authoritativeVisualHistory = new();
    private int _acceptedVisualMeasurementCount;
    private bool _hasRotationPropagationBaseline;
    private long _rotationPropagationVisualBaselineTimestampNs;
    private Pose _rotationPropagationCameraFromBoardVisual;
    private Pose _rotationPropagationWorldFromCameraVisual;
    private Quaternion _rotationPropagationNavigationFromCamera;
    private Quaternion _heldNavigationFromCamera;
    public long PropagationEvaluationTimestampNs { get; private set; }
    public long PropagationAttitudeTimestampNs { get; private set; }
    public double PropagationAttitudeAgeMilliseconds => PropagationAttitudeTimestampNs > 0L
        ? (PropagationEvaluationTimestampNs - PropagationAttitudeTimestampNs) / 1_000_000d
        : double.PositiveInfinity;
    private bool _hasHorizontalGnssWorldShadowBaseline;
    private TelemetryAircraftPositionSample _horizontalGnssWorldShadowBaseline;
    private double _horizontalGnssWorldShadowBaselineAgeMilliseconds;
    private long _horizontalGnssWorldShadowVisualBaselineTimestampNs;
    private Pose _horizontalGnssWorldShadowWorldFromCameraBaseline;
    private Quaternion _worldFromNavigationRotation;
    private long _horizontalGnssWorldShadowOutageStartTimestampNs;
    private long _horizontalGnssWorldShadowNoneStartTimestampNs;
    private long _horizontalGnssWorldShadowStaleTargetGapStartTimestampNs;
    private int _horizontalGnssWorldShadowStaleTargetGapCount;
    private double _horizontalGnssWorldShadowStaleTargetGapDurationMilliseconds;
    private DjiRenderPoseSource _horizontalGnssWorldShadowProductionRenderSource = DjiRenderPoseSource.None;
    private bool _hasPreviousSmoothedRawGnssCandidate;
    private long _previousSmoothedRawGnssTargetTimestampNs;
    private Vector2 _previousSmoothedRawGnssNavigationDelta;
    private bool _hasVerticalAltitudeBaseline;
    private TelemetryAircraftPositionSample _verticalAltitudeBaseline;
    private double _verticalAltitudeBaselineAgeMilliseconds;
    private long _verticalAltitudeVisualBaselineTimestampNs;
    private Pose _verticalAltitudeWorldFromCameraBaseline;
    private Quaternion _verticalAltitudeWorldFromNavigationRotation;

    public WorldLocalizationSnapshot Snapshot => _state.Snapshot;
    public int AcceptedVisualMeasurementCount => _acceptedVisualMeasurementCount;
    public WorldRotationPropagationEquivalenceSnapshot RotationPropagationEquivalence => _rotationPropagationEquivalence.Snapshot;
    public bool HasActiveRotationPropagation => _hasRotationPropagationBaseline;
    public int AuthoritativeVisualHistoryCount => _authoritativeVisualHistory.Count;
    public long LastProductionBaselineTimestampNs { get; private set; }
    public long LastResolvedWorldBaselineTimestampNs { get; private set; }
    public bool LastHistoricalBaselineResolved { get; private set; }
    public string LastBaselineResolveFailureReason { get; private set; } = string.Empty;
    public HorizontalGnssWorldShadowSnapshot LatestHorizontalGnssWorldShadow { get; private set; }
    public HorizontalGnssWorldShadowReacquisitionSnapshot LatestHorizontalGnssWorldShadowReacquisition { get; private set; }
    public HorizontalGnssWorldShadowOutageDiagnostics HorizontalGnssWorldShadowDiagnostics { get; private set; }
    public HorizontalGnssSmoothingShadowSnapshot LatestHorizontalGnssSmoothingShadow { get; private set; }
    public HorizontalGnssSmoothingReacquisitionSnapshot LatestHorizontalGnssSmoothingReacquisition { get; private set; }
    public bool ProductionHorizontalGnssApplied { get; private set; }
    public HorizontalGnssSmoothingState ProductionHorizontalGnssState { get; private set; } = HorizontalGnssSmoothingState.Uninitialized;
    public string ProductionHorizontalGnssReason { get; private set; } = string.Empty;
    public VerticalAltitudeProductionSnapshot ProductionVerticalAltitude { get; private set; }
    public bool HasActiveHorizontalGnssWorldShadow => _hasHorizontalGnssWorldShadowBaseline;
    public HorizontalGnssQualityAdmissionSnapshot HorizontalGnssQualityAdmission =>
        _horizontalGnssQualityAdmissionGate.Snapshot;

    /// <summary>
    /// Observes the existing AircraftLocation3D callback stream. The gate
    /// de-duplicates timestamps, so Unity polling cannot advance confirmation.
    /// </summary>
    public void ObserveHorizontalGnssQuality(int? satelliteCount, long positionCallbackTimestampNs)
    {
        _horizontalGnssQualityAdmissionGate.Update(positionCallbackTimestampNs, satelliteCount);
    }

    public void ResetHorizontalGnssQualityAdmission() => _horizontalGnssQualityAdmissionGate.Reset();

    public bool TryInitializeFromVisualMeasurement(
        Pose cameraFromBoard,
        long sourceTimestampNs,
        bool isAuthoritativeVisual,
        out string rejectionReason)
    {
        rejectionReason = string.Empty;
        if (!isAuthoritativeVisual)
        {
            rejectionReason = "NOT_AUTHORITATIVE_VISUAL";
            return false;
        }

        if (_state.HasWorld)
        {
            rejectionReason = "WORLD_ALREADY_INITIALIZED";
            return false;
        }

        if (!HasValidWorldInvariant(_state.Snapshot.worldFromBoard, cameraFromBoard))
        {
            rejectionReason = "WORLD_LOCALIZATION_INVARIANT_FAILED";
            return false;
        }

        var initialized = _state.InitializeFromVisual(cameraFromBoard, sourceTimestampNs, out rejectionReason);
        if (initialized)
        {
            RecordAuthoritativeVisualSnapshot(sourceTimestampNs);
            _acceptedVisualMeasurementCount++;
        }
        return initialized;
    }

    /// <summary>
    /// Applies a later authoritative visual measurement without replacing the
    /// established World frame.
    /// </summary>
    public bool TryUpdateFromVisualMeasurement(
        Pose cameraFromBoard,
        long sourceTimestampNs,
        bool isAuthoritativeVisual,
        out string rejectionReason)
    {
        return TryUpdateFromVisualMeasurement(
            cameraFromBoard,
            sourceTimestampNs,
            isAuthoritativeVisual,
            positionHistory: null,
            HorizontalGnssWorldShadowSnapshot.DefaultMaximumAgeMilliseconds,
            out rejectionReason);
    }

    /// <summary>
    /// Applies a visual pose and, when an outage was active, records a
    /// diagnostic-only GNSS candidate at the exact visual timestamp before
    /// authoritative world state is restored.
    /// </summary>
    public bool TryUpdateFromVisualMeasurement(
        Pose cameraFromBoard,
        long sourceTimestampNs,
        bool isAuthoritativeVisual,
        IReadOnlyList<TelemetryAircraftPositionSample> positionHistory,
        float maximumGnssAgeMilliseconds,
        out string rejectionReason)
    {
        rejectionReason = string.Empty;
        if (!isAuthoritativeVisual)
        {
            rejectionReason = "NOT_AUTHORITATIVE_VISUAL";
            return false;
        }

        if (!_state.HasWorld)
        {
            rejectionReason = "WORLD_NOT_INITIALIZED";
            return false;
        }

        if (!HasValidWorldInvariant(_state.Snapshot.worldFromBoard, cameraFromBoard))
        {
            rejectionReason = "WORLD_LOCALIZATION_INVARIANT_FAILED";
            return false;
        }

        var hadDiagnosticOutage = _hasHorizontalGnssWorldShadowBaseline;
        var preVisualCandidate = default(HorizontalGnssWorldShadowSnapshot);
        var preVisualSmoothedCandidate = default(HorizontalGnssSmoothingShadowSnapshot);
        if (hadDiagnosticOutage)
        {
            preVisualCandidate = BuildHorizontalGnssWorldShadowCandidate(
                sourceTimestampNs,
                _horizontalGnssWorldShadowWorldFromCameraBaseline.rotation,
                positionHistory,
                maximumGnssAgeMilliseconds,
                hasCandidateRotation: false);
            LatestHorizontalGnssWorldShadow = preVisualCandidate;
            preVisualSmoothedCandidate = BuildHorizontalGnssSmoothingShadowCandidate(
                preVisualCandidate,
                sourceTimestampNs,
                _horizontalGnssWorldShadowWorldFromCameraBaseline.rotation,
                hasCandidateRotation: false);
            LatestHorizontalGnssSmoothingShadow = preVisualSmoothedCandidate;
            UpdateHorizontalGnssWorldShadowDiagnostics(
                preVisualCandidate,
                DjiRenderPoseSource.Visual,
                sourceTimestampNs);
        }

        var updated = _state.UpdateFromVisual(cameraFromBoard, sourceTimestampNs, out rejectionReason);
        if (updated)
        {
            if (hadDiagnosticOutage && preVisualCandidate.valid)
                RecordHorizontalGnssWorldShadowReacquisition(preVisualCandidate, _state.Snapshot.worldFromCamera);
            else if (hadDiagnosticOutage)
            {
                LatestHorizontalGnssWorldShadowReacquisition = new HorizontalGnssWorldShadowReacquisitionSnapshot(
                    hasComparison: false,
                    visualBaselineTimestampNs: preVisualCandidate.visualBaselineTimestampNs,
                    targetTimestampNs: sourceTimestampNs,
                    candidateWorldFromCamera: Pose.identity,
                    visualWorldFromCamera: _state.Snapshot.worldFromCamera,
                    worldPositionError: Vector3.zero,
                    navigationHorizontalError: Vector2.zero,
                    navigationHorizontalErrorMagnitudeMeters: float.NaN,
                    candidateAgeMilliseconds: preVisualCandidate.gnssTargetAgeMilliseconds);
                HorizontalGnssWorldShadowDiagnostics = HorizontalGnssWorldShadowDiagnostics.WithReacquisition(
                    candidateValid: false,
                    candidateAgeMilliseconds: preVisualCandidate.gnssTargetAgeMilliseconds,
                    horizontalResidualMeters: float.NaN);
            }
            if (hadDiagnosticOutage)
                RecordHorizontalGnssSmoothingReacquisition(
                    preVisualCandidate,
                    preVisualSmoothedCandidate,
                    _state.Snapshot.worldFromCamera);
            StopRotationPropagation();
            EndHorizontalGnssWorldShadowOutage();
            RecordAuthoritativeVisualSnapshot(sourceTimestampNs);
            _acceptedVisualMeasurementCount++;
        }
        return updated;
    }

    /// <summary>
    /// Captures the same authoritative visual/telemetry baseline that the
    /// production rotation-only path uses. It does not change world state
    /// until an actual production propagation sample arrives.
    /// </summary>
    public bool TryBeginRotationPropagation(
        Pose cameraFromBoardVisual,
        long visualBaselineTimestampNs,
        Quaternion navigationFromCameraBaseline,
        out string rejectionReason)
    {
        return TryBeginRotationPropagation(
            cameraFromBoardVisual,
            visualBaselineTimestampNs,
            navigationFromCameraBaseline,
            positionHistory: null,
            HorizontalGnssWorldShadowSnapshot.DefaultMaximumAgeMilliseconds,
            out rejectionReason);
    }

    /// <summary>
    /// Starts the existing rotation shadow and independently captures a
    /// causal horizontal GNSS baseline. GNSS availability never rejects the
    /// production-equivalent rotation propagation interval.
    /// </summary>
    public bool TryBeginRotationPropagation(
        Pose cameraFromBoardVisual,
        long visualBaselineTimestampNs,
        Quaternion navigationFromCameraBaseline,
        IReadOnlyList<TelemetryAircraftPositionSample> positionHistory,
        float maximumGnssAgeMilliseconds,
        out string rejectionReason)
    {
        rejectionReason = string.Empty;
        LastProductionBaselineTimestampNs = visualBaselineTimestampNs;
        LastResolvedWorldBaselineTimestampNs = 0L;
        LastHistoricalBaselineResolved = false;
        LastBaselineResolveFailureReason = string.Empty;
        var snapshot = _state.Snapshot;
        if (!snapshot.hasWorld)
        {
            rejectionReason = "WORLD_NOT_INITIALIZED";
            LastBaselineResolveFailureReason = rejectionReason;
            LatestHorizontalGnssWorldShadow = HorizontalGnssWorldShadowSnapshot.Invalid(
                "NO_EXACT_WORLD_BASELINE", visualBaselineTimestampNs);
            return false;
        }

        if (!TryResolveAuthoritativeVisualSnapshot(
                visualBaselineTimestampNs,
                out var resolvedVisualBaseline))
        {
            rejectionReason = "PRODUCTION_BASELINE_NOT_IN_WORLD_HISTORY";
            LastBaselineResolveFailureReason = rejectionReason;
            LatestHorizontalGnssWorldShadow = HorizontalGnssWorldShadowSnapshot.Invalid(
                "NO_EXACT_WORLD_BASELINE", visualBaselineTimestampNs);
            return false;
        }

        if (!IsFiniteRigidPose(cameraFromBoardVisual) || !IsFiniteQuaternion(navigationFromCameraBaseline))
        {
            rejectionReason = "INVALID_ROTATION_PROPAGATION_BASELINE";
            LastBaselineResolveFailureReason = rejectionReason;
            LatestHorizontalGnssWorldShadow = HorizontalGnssWorldShadowSnapshot.Invalid(
                "NO_NAVIGATION_ALIGNMENT", visualBaselineTimestampNs);
            return false;
        }

        _rotationPropagationVisualBaselineTimestampNs = visualBaselineTimestampNs;
        _rotationPropagationCameraFromBoardVisual = cameraFromBoardVisual;
        _rotationPropagationWorldFromCameraVisual = resolvedVisualBaseline.worldFromCamera;
        _rotationPropagationNavigationFromCamera = Quaternion.Normalize(navigationFromCameraBaseline);
        _heldNavigationFromCamera = _rotationPropagationNavigationFromCamera;
        PropagationEvaluationTimestampNs = 0L;
        PropagationAttitudeTimestampNs = 0L;
        _hasRotationPropagationBaseline = true;
        LastResolvedWorldBaselineTimestampNs = resolvedVisualBaseline.sourceTimestampNs;
        LastHistoricalBaselineResolved = true;
        BeginHorizontalGnssWorldShadow(
            resolvedVisualBaseline,
            navigationFromCameraBaseline,
            positionHistory,
            maximumGnssAgeMilliseconds);
        BeginVerticalAltitudePropagation(
            resolvedVisualBaseline,
            navigationFromCameraBaseline,
            positionHistory,
            VerticalAltitudeSmoothingEstimator.DefaultMaximumAgeMilliseconds);
        return true;
    }

    /// <summary>
    /// Mirrors one existing production prediction. Rotation math is shared via
    /// ReferenceFrameTransforms; this shadow never chooses a render pose.
    /// </summary>
    public bool TryUpdateRotationPropagation(
        long telemetryTimestampNs,
        Quaternion navigationFromCameraTarget,
        Pose productionCameraFromBoard,
        out string rejectionReason)
    {
        return TryUpdateRotationPropagation(
            telemetryTimestampNs,
            navigationFromCameraTarget,
            productionCameraFromBoard,
            positionHistory: null,
            HorizontalGnssWorldShadowSnapshot.DefaultMaximumAgeMilliseconds,
            out rejectionReason);
    }

    public bool TryUpdateRotationPropagation(
        long telemetryTimestampNs,
        Quaternion navigationFromCameraTarget,
        Pose productionCameraFromBoard,
        IReadOnlyList<TelemetryAircraftPositionSample> positionHistory,
        float maximumGnssAgeMilliseconds,
        out string rejectionReason)
    {
        if (!IsFiniteQuaternion(navigationFromCameraTarget))
        {
            rejectionReason = "INVALID_TARGET_TELEMETRY_ROTATION";
            return false;
        }

        return TryEvaluateRotationPropagation(
            telemetryTimestampNs, telemetryTimestampNs, navigationFromCameraTarget,
            productionCameraFromBoard, positionHistory, maximumGnssAgeMilliseconds,
            out rejectionReason);
    }

    /// <summary>
    /// Evaluates position at advancing monotonic time, independently of attitude
    /// delivery. Rotation is held, never extrapolated, between causal samples.
    /// </summary>
    public bool TryEvaluateRotationPropagation(
        long evaluationTimestampNs,
        long attitudeMeasurementTimestampNs,
        Quaternion navigationFromCameraTarget,
        Pose productionCameraFromBoard,
        IReadOnlyList<TelemetryAircraftPositionSample> positionHistory,
        float maximumGnssAgeMilliseconds,
        out string rejectionReason)
    {
        rejectionReason = string.Empty;
        if (!_hasRotationPropagationBaseline)
        {
            rejectionReason = "PROPAGATION_NOT_ACTIVE";
            return false;
        }

        if (evaluationTimestampNs <= _rotationPropagationVisualBaselineTimestampNs ||
            evaluationTimestampNs <= _state.Snapshot.lastPropagationTimestampNs)
        {
            rejectionReason = "NON_INCREASING_PROPAGATION_TIMESTAMP";
            return false;
        }

        var acceptedNewAttitude = false;
        if (attitudeMeasurementTimestampNs >= _rotationPropagationVisualBaselineTimestampNs &&
            attitudeMeasurementTimestampNs > PropagationAttitudeTimestampNs &&
            attitudeMeasurementTimestampNs <= evaluationTimestampNs &&
            IsFiniteQuaternion(navigationFromCameraTarget))
        {
            _heldNavigationFromCamera = Quaternion.Normalize(navigationFromCameraTarget);
            PropagationAttitudeTimestampNs = attitudeMeasurementTimestampNs;
            acceptedNewAttitude = true;
        }
        PropagationEvaluationTimestampNs = evaluationTimestampNs;

        var rotationOnlyCameraFromBoard = ReferenceFrameTransforms.PredictCameraFromBoardRotationOnly(
            _rotationPropagationCameraFromBoardVisual,
            _rotationPropagationNavigationFromCamera,
            _heldNavigationFromCamera);

        // Build all diagnostic candidates from the same exact Step 7B visual
        // baseline before promoting the GNSS-only smoother into this bounded
        // production propagation interval.
        var rotationOnlyWorldFromCamera = CalculateWorldFromCamera(
            _state.Snapshot.worldFromBoard,
            rotationOnlyCameraFromBoard);
        LatestHorizontalGnssWorldShadow = BuildHorizontalGnssWorldShadowCandidate(
            evaluationTimestampNs,
            rotationOnlyWorldFromCamera.rotation,
            positionHistory,
            maximumGnssAgeMilliseconds,
            hasCandidateRotation: true);
        LatestHorizontalGnssSmoothingShadow = BuildHorizontalGnssSmoothingShadowCandidate(
            LatestHorizontalGnssWorldShadow,
            evaluationTimestampNs,
            rotationOnlyWorldFromCamera.rotation,
            hasCandidateRotation: true);
        ProductionVerticalAltitude = BuildVerticalAltitudeProductionCandidate(
            evaluationTimestampNs,
            positionHistory);

        var propagatedWorldFromCamera = rotationOnlyWorldFromCamera;
        ApplyProductionNavigationTranslation(
            LatestHorizontalGnssSmoothingShadow,
            ProductionVerticalAltitude,
            rotationOnlyWorldFromCamera,
            ref propagatedWorldFromCamera);
        var propagatedCameraFromBoard = CalculateCameraFromBoard(
            propagatedWorldFromCamera,
            _state.Snapshot.worldFromBoard);
        if (!_state.UpdateFromResolvedVisualBaselineRotationPropagation(
                propagatedCameraFromBoard,
                _rotationPropagationCameraFromBoardVisual,
                _rotationPropagationWorldFromCameraVisual,
                _rotationPropagationVisualBaselineTimestampNs,
                evaluationTimestampNs,
                out rejectionReason))
        {
            return false;
        }

        if (!WorldLocalizationPoseResolver.TryGetCameraFromBoard(_state.Snapshot, out var worldCameraFromBoard))
        {
            rejectionReason = "WORLD_CAMERA_POSE_UNAVAILABLE";
            return false;
        }

        if (acceptedNewAttitude)
        {
            _rotationPropagationEquivalence.TryRecord(
                PropagationAttitudeTimestampNs,
                productionCameraFromBoard,
                rotationOnlyCameraFromBoard,
                productionBaselineTimestampNs: _rotationPropagationVisualBaselineTimestampNs,
                resolvedWorldBaselineTimestampNs: LastResolvedWorldBaselineTimestampNs,
                historicalBaselineResolved: true,
                worldBaselineHistoryAgeMs: LatestWorldVisualHistoryAgeMs,
                worldBaselineHistoryCount: _authoritativeVisualHistory.Count,
                baselineResolveFailureReason: string.Empty);
        }
        UpdateHorizontalGnssWorldShadowDiagnostics(
            LatestHorizontalGnssWorldShadow,
            DjiRenderPoseSource.TelemetryPropagated,
            evaluationTimestampNs);
        return true;
    }

    /// <summary>
    /// Ends only the shadow propagation interval. The localized World remains
    /// available for a later authoritative visual update.
    /// </summary>
    public void StopRotationPropagation()
    {
        _hasRotationPropagationBaseline = false;
        _rotationPropagationVisualBaselineTimestampNs = 0L;
        _rotationPropagationCameraFromBoardVisual = Pose.identity;
        _rotationPropagationWorldFromCameraVisual = Pose.identity;
        _rotationPropagationNavigationFromCamera = Quaternion.identity;
        _heldNavigationFromCamera = Quaternion.identity;
        PropagationEvaluationTimestampNs = 0L;
        PropagationAttitudeTimestampNs = 0L;
    }

    /// <summary>
    /// Ends an explicit localization session or an irrecoverable diagnostic
    /// baseline. Normal production timeout deliberately does not call this:
    /// the horizontal-only GNSS diagnostic can continue while rendering is NONE.
    /// </summary>
    public void ResetHorizontalGnssWorldShadowOutage()
    {
        _hasHorizontalGnssWorldShadowBaseline = false;
        _horizontalGnssWorldShadowBaseline = default;
        _horizontalGnssWorldShadowBaselineAgeMilliseconds = double.PositiveInfinity;
        _horizontalGnssWorldShadowVisualBaselineTimestampNs = 0L;
        _horizontalGnssWorldShadowWorldFromCameraBaseline = Pose.identity;
        _worldFromNavigationRotation = Quaternion.identity;
        _horizontalGnssWorldShadowOutageStartTimestampNs = 0L;
        _horizontalGnssWorldShadowNoneStartTimestampNs = 0L;
        _horizontalGnssWorldShadowStaleTargetGapStartTimestampNs = 0L;
        _horizontalGnssWorldShadowStaleTargetGapCount = 0;
        _horizontalGnssWorldShadowStaleTargetGapDurationMilliseconds = 0d;
        _horizontalGnssWorldShadowProductionRenderSource = DjiRenderPoseSource.None;
        _hasPreviousSmoothedRawGnssCandidate = false;
        _previousSmoothedRawGnssTargetTimestampNs = 0L;
        _previousSmoothedRawGnssNavigationDelta = Vector2.zero;
        HorizontalGnssWorldShadowDiagnostics = default;
        _horizontalGnssSmoothingEstimator.Reset();
        LatestHorizontalGnssSmoothingShadow = default;
        LatestHorizontalGnssSmoothingReacquisition = default;
        ProductionHorizontalGnssApplied = false;
        ProductionHorizontalGnssState = HorizontalGnssSmoothingState.Uninitialized;
        ProductionHorizontalGnssReason = string.Empty;
        _hasVerticalAltitudeBaseline = false;
        _verticalAltitudeBaseline = default;
        _verticalAltitudeBaselineAgeMilliseconds = double.PositiveInfinity;
        _verticalAltitudeVisualBaselineTimestampNs = 0L;
        _verticalAltitudeWorldFromCameraBaseline = Pose.identity;
        _verticalAltitudeWorldFromNavigationRotation = Quaternion.identity;
        _verticalAltitudeSmoothingEstimator.Reset();
        ProductionVerticalAltitude = VerticalAltitudeProductionSnapshot.BaselineUnavailable(0L, "BASELINE_UNAVAILABLE");
    }

    private void BeginHorizontalGnssWorldShadow(
        AuthoritativeWorldVisualSnapshot resolvedVisualBaseline,
        Quaternion navigationFromCameraBaseline,
        IReadOnlyList<TelemetryAircraftPositionSample> positionHistory,
        float maximumGnssAgeMilliseconds)
    {
        ResetHorizontalGnssWorldShadowOutage();
        LatestHorizontalGnssWorldShadowReacquisition = default;

        if (!TryGetCausalPositionSample(positionHistory, resolvedVisualBaseline.sourceTimestampNs, out var baselineSample))
        {
            LatestHorizontalGnssWorldShadow = HorizontalGnssWorldShadowSnapshot.Invalid(
                "NO_CAUSAL_GNSS_BASELINE", resolvedVisualBaseline.sourceTimestampNs);
            return;
        }

        if (!DjiAircraftPositionFreshness.IsFreshAtOrBefore(
                baselineSample.timestampNs,
                resolvedVisualBaseline.sourceTimestampNs,
                maximumGnssAgeMilliseconds,
                out var baselineAgeMilliseconds))
        {
            LatestHorizontalGnssWorldShadow = HorizontalGnssWorldShadowSnapshot.Invalid(
                "GNSS_BASELINE_STALE",
                resolvedVisualBaseline.sourceTimestampNs,
                baselineSample.timestampNs,
                baselineAgeMilliseconds);
            return;
        }

        var worldFromNavigation = Quaternion.Normalize(
            resolvedVisualBaseline.worldFromCamera.rotation * Quaternion.Inverse(Quaternion.Normalize(navigationFromCameraBaseline)));
        if (!IsFiniteQuaternion(worldFromNavigation))
        {
            LatestHorizontalGnssWorldShadow = HorizontalGnssWorldShadowSnapshot.Invalid(
                "NO_NAVIGATION_ALIGNMENT",
                resolvedVisualBaseline.sourceTimestampNs,
                baselineSample.timestampNs,
                baselineAgeMilliseconds);
            return;
        }

        _hasHorizontalGnssWorldShadowBaseline = true;
        _horizontalGnssWorldShadowBaseline = baselineSample;
        _horizontalGnssWorldShadowBaselineAgeMilliseconds = baselineAgeMilliseconds;
        _horizontalGnssWorldShadowVisualBaselineTimestampNs = resolvedVisualBaseline.sourceTimestampNs;
        _horizontalGnssWorldShadowWorldFromCameraBaseline = resolvedVisualBaseline.worldFromCamera;
        _worldFromNavigationRotation = worldFromNavigation;
        _horizontalGnssWorldShadowOutageStartTimestampNs = resolvedVisualBaseline.sourceTimestampNs;
        _horizontalGnssWorldShadowProductionRenderSource = DjiRenderPoseSource.TelemetryPropagated;
        _horizontalGnssSmoothingEstimator.Initialize(resolvedVisualBaseline.sourceTimestampNs);
        LatestHorizontalGnssWorldShadow = HorizontalGnssWorldShadowSnapshot.AwaitingTarget(
            resolvedVisualBaseline.sourceTimestampNs,
            baselineSample.timestampNs,
            baselineAgeMilliseconds,
            worldFromNavigation);
        LatestHorizontalGnssSmoothingShadow = HorizontalGnssSmoothingShadowSnapshot.AwaitingTarget(
            resolvedVisualBaseline.sourceTimestampNs,
            baselineSample.timestampNs,
            baselineAgeMilliseconds,
            worldFromNavigation,
            _horizontalGnssSmoothingEstimator.Update(
                resolvedVisualBaseline.sourceTimestampNs,
                hasFreshGnss: true,
                rawGnssTarget: Vector2.zero,
                rawGnssTimestampNs: baselineSample.timestampNs));
    }

    /// <summary>
    /// Captures one causal, fresh AircraftLocation3D altitude sample for the
    /// current visual baseline. Horizontal availability is intentionally not a
    /// prerequisite: vertical can become available independently.
    /// </summary>
    private void BeginVerticalAltitudePropagation(
        AuthoritativeWorldVisualSnapshot resolvedVisualBaseline,
        Quaternion navigationFromCameraBaseline,
        IReadOnlyList<TelemetryAircraftPositionSample> positionHistory,
        float maximumAgeMilliseconds)
    {
        _hasVerticalAltitudeBaseline = false;
        _verticalAltitudeSmoothingEstimator.Reset();
        ProductionVerticalAltitude = VerticalAltitudeProductionSnapshot.BaselineUnavailable(
            resolvedVisualBaseline.sourceTimestampNs,
            "NO_CAUSAL_ALTITUDE_BASELINE");

        if (!TryGetCausalPositionSample(positionHistory, resolvedVisualBaseline.sourceTimestampNs, out var baselineSample))
            return;

        if (!DjiAircraftPositionFreshness.IsFreshAtOrBefore(
                baselineSample.timestampNs,
                resolvedVisualBaseline.sourceTimestampNs,
                maximumAgeMilliseconds,
                out var baselineAgeMilliseconds))
        {
            ProductionVerticalAltitude = VerticalAltitudeProductionSnapshot.BaselineUnavailable(
                resolvedVisualBaseline.sourceTimestampNs,
                "ALTITUDE_BASELINE_STALE");
            return;
        }

        var worldFromNavigation = Quaternion.Normalize(
            resolvedVisualBaseline.worldFromCamera.rotation * Quaternion.Inverse(Quaternion.Normalize(navigationFromCameraBaseline)));
        if (!IsFiniteQuaternion(worldFromNavigation))
        {
            ProductionVerticalAltitude = VerticalAltitudeProductionSnapshot.BaselineUnavailable(
                resolvedVisualBaseline.sourceTimestampNs,
                "NO_NAVIGATION_ALIGNMENT");
            return;
        }

        _hasVerticalAltitudeBaseline = _verticalAltitudeSmoothingEstimator.Initialize(resolvedVisualBaseline.sourceTimestampNs);
        _verticalAltitudeBaseline = baselineSample;
        _verticalAltitudeBaselineAgeMilliseconds = baselineAgeMilliseconds;
        _verticalAltitudeVisualBaselineTimestampNs = resolvedVisualBaseline.sourceTimestampNs;
        _verticalAltitudeWorldFromCameraBaseline = resolvedVisualBaseline.worldFromCamera;
        _verticalAltitudeWorldFromNavigationRotation = worldFromNavigation;
        ProductionVerticalAltitude = new VerticalAltitudeProductionSnapshot(
            baselineAvailable: _hasVerticalAltitudeBaseline,
            sourceFresh: true,
            baselineAltitude: baselineSample.altitude,
            currentAltitude: baselineSample.altitude,
            baselineAltitudeTimestampNs: baselineSample.timestampNs,
            altitudeSourceTimestampNs: baselineSample.timestampNs,
            altitudeAgeMilliseconds: baselineAgeMilliseconds,
            rawDeltaAltitudeUp: 0f,
            rawDeltaNavigationDown: 0f,
            smoothedDeltaNavigationDown: 0f,
            worldVerticalContribution: Vector3.zero,
            estimate: _verticalAltitudeSmoothingEstimator.Update(
                resolvedVisualBaseline.sourceTimestampNs,
                hasFreshAltitude: true,
                rawTargetNavigationDown: 0f,
                altitudeSourceTimestampNs: baselineSample.timestampNs));
    }

    private VerticalAltitudeProductionSnapshot BuildVerticalAltitudeProductionCandidate(
        long targetTimestampNs,
        IReadOnlyList<TelemetryAircraftPositionSample> positionHistory)
    {
        if (!_hasVerticalAltitudeBaseline || !_verticalAltitudeSmoothingEstimator.IsInitialized)
            return VerticalAltitudeProductionSnapshot.BaselineUnavailable(targetTimestampNs, "BASELINE_UNAVAILABLE");

        if (!TryGetCausalPositionSample(positionHistory, targetTimestampNs, out var targetSample))
        {
            var stale = _verticalAltitudeSmoothingEstimator.Update(targetTimestampNs, false, 0f, 0L);
            return new VerticalAltitudeProductionSnapshot(
                true, false, _verticalAltitudeBaseline.altitude, double.NaN,
                _verticalAltitudeBaseline.timestampNs, 0L, double.PositiveInfinity,
                0f, 0f, stale.smoothedNavigationDown,
                _verticalAltitudeWorldFromNavigationRotation * new Vector3(0f, 0f, stale.smoothedNavigationDown),
                stale);
        }

        var fresh = DjiAircraftPositionFreshness.IsFreshAtOrBefore(
            targetSample.timestampNs,
            targetTimestampNs,
            VerticalAltitudeSmoothingEstimator.DefaultMaximumAgeMilliseconds,
            out var ageMilliseconds);
        var rawDeltaAltitudeUp = fresh ? (float)(targetSample.altitude - _verticalAltitudeBaseline.altitude) : 0f;
        // Location3D altitude is physically validated positive-up; project
        // navigation is N/E/Down, hence the required sign inversion.
        var rawDeltaNavigationDown = -rawDeltaAltitudeUp;
        var estimate = _verticalAltitudeSmoothingEstimator.Update(
            targetTimestampNs,
            fresh,
            rawDeltaNavigationDown,
            fresh ? targetSample.timestampNs : 0L);
        var worldContribution = _verticalAltitudeWorldFromNavigationRotation *
                                new Vector3(0f, 0f, estimate.smoothedNavigationDown);
        return new VerticalAltitudeProductionSnapshot(
            baselineAvailable: true,
            sourceFresh: fresh,
            baselineAltitude: _verticalAltitudeBaseline.altitude,
            currentAltitude: fresh ? targetSample.altitude : double.NaN,
            baselineAltitudeTimestampNs: _verticalAltitudeBaseline.timestampNs,
            altitudeSourceTimestampNs: fresh ? targetSample.timestampNs : 0L,
            altitudeAgeMilliseconds: ageMilliseconds,
            rawDeltaAltitudeUp: rawDeltaAltitudeUp,
            rawDeltaNavigationDown: rawDeltaNavigationDown,
            smoothedDeltaNavigationDown: estimate.smoothedNavigationDown,
            worldVerticalContribution: worldContribution,
            estimate: estimate);
    }

    private HorizontalGnssWorldShadowSnapshot BuildHorizontalGnssWorldShadowCandidate(
        long targetTimestampNs,
        Quaternion candidateWorldFromCameraRotation,
        IReadOnlyList<TelemetryAircraftPositionSample> positionHistory,
        float maximumGnssAgeMilliseconds,
        bool hasCandidateRotation)
    {
        if (!_hasHorizontalGnssWorldShadowBaseline)
            return LatestHorizontalGnssWorldShadow.valid
                ? HorizontalGnssWorldShadowSnapshot.Invalid("NO_CAUSAL_GNSS_BASELINE", _horizontalGnssWorldShadowVisualBaselineTimestampNs)
                : LatestHorizontalGnssWorldShadow;

        if (!TryGetCausalPositionSample(positionHistory, targetTimestampNs, out var targetSample))
        {
            return HorizontalGnssWorldShadowSnapshot.Invalid(
                "NO_CAUSAL_GNSS_TARGET",
                _horizontalGnssWorldShadowVisualBaselineTimestampNs,
                _horizontalGnssWorldShadowBaseline.timestampNs,
                _horizontalGnssWorldShadowBaselineAgeMilliseconds,
                targetTimestampNs);
        }

        if (!DjiAircraftPositionFreshness.IsFreshAtOrBefore(
                targetSample.timestampNs,
                targetTimestampNs,
                maximumGnssAgeMilliseconds,
                out var targetAgeMilliseconds))
        {
            return HorizontalGnssWorldShadowSnapshot.Invalid(
                "GNSS_TARGET_STALE",
                _horizontalGnssWorldShadowVisualBaselineTimestampNs,
                _horizontalGnssWorldShadowBaseline.timestampNs,
                _horizontalGnssWorldShadowBaselineAgeMilliseconds,
                targetTimestampNs,
                targetSample.timestampNs,
                targetAgeMilliseconds);
        }

        var horizontalNed = DjiGeodesy.HorizontalNedDelta(
            _horizontalGnssWorldShadowBaseline.latitudeDegrees,
            _horizontalGnssWorldShadowBaseline.longitudeDegrees,
            targetSample.latitudeDegrees,
            targetSample.longitudeDegrees);
        var navigationDelta = new Vector3(horizontalNed.x, horizontalNed.y, 0f);
        var worldDelta = _worldFromNavigationRotation * navigationDelta;
        var candidate = new Pose(
            _horizontalGnssWorldShadowWorldFromCameraBaseline.position + worldDelta,
            Quaternion.Normalize(candidateWorldFromCameraRotation));
        return HorizontalGnssWorldShadowSnapshot.Valid(
            _horizontalGnssWorldShadowVisualBaselineTimestampNs,
            _horizontalGnssWorldShadowBaseline.timestampNs,
            _horizontalGnssWorldShadowBaselineAgeMilliseconds,
            targetTimestampNs,
            targetSample.timestampNs,
            targetAgeMilliseconds,
            navigationDelta,
            worldDelta,
            candidate,
            _worldFromNavigationRotation,
            hasCandidateRotation);
    }

    private HorizontalGnssSmoothingShadowSnapshot BuildHorizontalGnssSmoothingShadowCandidate(
        HorizontalGnssWorldShadowSnapshot rawCandidate,
        long targetTimestampNs,
        Quaternion candidateWorldFromCameraRotation,
        bool hasCandidateRotation)
    {
        if (!_hasHorizontalGnssWorldShadowBaseline || !_horizontalGnssSmoothingEstimator.IsInitialized)
        {
            return HorizontalGnssSmoothingShadowSnapshot.Invalid(
                "NO_CAUSAL_GNSS_BASELINE",
                targetTimestampNs,
                _horizontalGnssWorldShadowVisualBaselineTimestampNs);
        }

        var quality = _horizontalGnssQualityAdmissionGate.Snapshot;
        // The coordinator remains compatible with isolated transform tests
        // until live satellite telemetry has supplied its first callback.
        var qualityBlocksHorizontalAdmission = quality.hasObservedGnssCallback && !quality.trusted;
        var estimate = qualityBlocksHorizontalAdmission
            ? _horizontalGnssSmoothingEstimator.FreezeForGnssQuality(
                targetTimestampNs,
                quality.admissionReason)
            : _horizontalGnssSmoothingEstimator.Update(
                targetTimestampNs,
                rawCandidate.valid,
                rawCandidate.valid
                    ? new Vector2(rawCandidate.navigationDelta.x, rawCandidate.navigationDelta.y)
                    : Vector2.zero,
                rawCandidate.valid ? rawCandidate.gnssTargetTimestampNs : 0L);
        var navigationDelta = new Vector3(
            estimate.smoothedNavigationDelta.x,
            estimate.smoothedNavigationDelta.y,
            0f);
        var worldDelta = _worldFromNavigationRotation * navigationDelta;
        var candidateWorldFromCamera = new Pose(
            _horizontalGnssWorldShadowWorldFromCameraBaseline.position + worldDelta,
            Quaternion.Normalize(candidateWorldFromCameraRotation));
        var rawStepMagnitude = 0f;
        if (rawCandidate.valid && rawCandidate.gnssTargetTimestampNs > _previousSmoothedRawGnssTargetTimestampNs)
        {
            var rawNavigationDelta = new Vector2(rawCandidate.navigationDelta.x, rawCandidate.navigationDelta.y);
            rawStepMagnitude = _hasPreviousSmoothedRawGnssCandidate
                ? Vector2.Distance(rawNavigationDelta, _previousSmoothedRawGnssNavigationDelta)
                : 0f;
            _previousSmoothedRawGnssNavigationDelta = rawNavigationDelta;
            _previousSmoothedRawGnssTargetTimestampNs = rawCandidate.gnssTargetTimestampNs;
            _hasPreviousSmoothedRawGnssCandidate = true;
        }

        return HorizontalGnssSmoothingShadowSnapshot.FromEstimate(
            _horizontalGnssWorldShadowVisualBaselineTimestampNs,
            targetTimestampNs,
            rawCandidate,
            estimate,
            navigationDelta,
            worldDelta,
            candidateWorldFromCamera,
            _worldFromNavigationRotation,
            hasCandidateRotation,
            rawStepMagnitude);
    }

    private void ApplyProductionNavigationTranslation(
        HorizontalGnssSmoothingShadowSnapshot smoothingSnapshot,
        VerticalAltitudeProductionSnapshot verticalSnapshot,
        Pose rotationOnlyWorldFromCamera,
        ref Pose propagatedWorldFromCamera)
    {
        ProductionHorizontalGnssApplied = false;
        ProductionHorizontalGnssState = smoothingSnapshot.estimate.state;
        ProductionHorizontalGnssReason = smoothingSnapshot.estimate.failureReason;

        var hasHorizontal = smoothingSnapshot.estimate.hasEstimate;
        var hasVertical = verticalSnapshot.hasContribution &&
                          Mathf.Abs(verticalSnapshot.smoothedDeltaNavigationDown) > 0.000001f;
        if (!hasHorizontal && !hasVertical)
            return;

        // Compose both independently smoothed N/E/Down contributions in the
        // fixed NED navigation frame before a single world-frame transform.
        // This deliberately avoids assuming Unity's Y/Z axes or overwriting
        // one source with the other after conversion.
        var horizontalNed = hasHorizontal
            ? smoothingSnapshot.navigationDelta
            : Vector3.zero;
        var verticalDown = hasVertical
            ? verticalSnapshot.smoothedDeltaNavigationDown
            : 0f;
        var navigationDelta = new Vector3(horizontalNed.x, horizontalNed.y, verticalDown);
        var worldFromNavigation = hasHorizontal
            ? smoothingSnapshot.worldFromNavigationRotation
            : _verticalAltitudeWorldFromNavigationRotation;
        var baselineWorldFromCamera = hasHorizontal
            ? _horizontalGnssWorldShadowWorldFromCameraBaseline
            : _verticalAltitudeWorldFromCameraBaseline;
        var translatedPosition = baselineWorldFromCamera.position + worldFromNavigation * navigationDelta;
        propagatedWorldFromCamera = new Pose(
            translatedPosition,
            rotationOnlyWorldFromCamera.rotation);
        ProductionHorizontalGnssApplied = hasHorizontal;
    }

    private static Pose CalculateWorldFromCamera(Pose worldFromBoard, Pose cameraFromBoard) =>
        ReferenceFrameTransforms.PoseFromMatrix(
            ReferenceFrameTransforms.Compose(
                ReferenceFrameTransforms.WorldFromBoard(worldFromBoard),
                ReferenceFrameTransforms.MatrixFromPose(
                    ReferenceFrameTransforms.Invert(cameraFromBoard))));

    private static Pose CalculateCameraFromBoard(Pose worldFromCamera, Pose worldFromBoard) =>
        ReferenceFrameTransforms.PoseFromMatrix(
            ReferenceFrameTransforms.Compose(
                ReferenceFrameTransforms.CameraFromWorld(worldFromCamera),
                ReferenceFrameTransforms.WorldFromBoard(worldFromBoard)));

    /// <summary>
    /// Evaluates only the retained horizontal position diagnostic after the
    /// production rotation path has expired. It never updates WorldLocalizationState
    /// or creates a post-timeout rotation prediction.
    /// </summary>
    public void UpdateHorizontalGnssWorldShadowDuringBoardHidden(
        long targetTimestampNs,
        DjiRenderPoseSource productionRenderSource,
        IReadOnlyList<TelemetryAircraftPositionSample> positionHistory,
        float maximumGnssAgeMilliseconds)
    {
        if (!_hasHorizontalGnssWorldShadowBaseline || targetTimestampNs <= 0L)
            return;

        _horizontalGnssWorldShadowProductionRenderSource = productionRenderSource;
        if (productionRenderSource == DjiRenderPoseSource.None &&
            _horizontalGnssWorldShadowNoneStartTimestampNs <= 0L)
        {
            _horizontalGnssWorldShadowNoneStartTimestampNs = targetTimestampNs;
        }

        if (productionRenderSource != DjiRenderPoseSource.None)
            return;

        LatestHorizontalGnssWorldShadow = BuildHorizontalGnssWorldShadowCandidate(
            targetTimestampNs,
            _horizontalGnssWorldShadowWorldFromCameraBaseline.rotation,
            positionHistory,
            maximumGnssAgeMilliseconds,
            hasCandidateRotation: false);
        LatestHorizontalGnssSmoothingShadow = BuildHorizontalGnssSmoothingShadowCandidate(
            LatestHorizontalGnssWorldShadow,
            targetTimestampNs,
            _horizontalGnssWorldShadowWorldFromCameraBaseline.rotation,
            hasCandidateRotation: false);
        UpdateHorizontalGnssWorldShadowDiagnostics(
            LatestHorizontalGnssWorldShadow,
            productionRenderSource,
            targetTimestampNs);
    }

    private void UpdateHorizontalGnssWorldShadowDiagnostics(
        HorizontalGnssWorldShadowSnapshot candidate,
        DjiRenderPoseSource productionRenderSource,
        long targetTimestampNs)
    {
        _horizontalGnssWorldShadowProductionRenderSource = productionRenderSource;
        var targetIsTemporarilyUnavailable = !candidate.valid &&
                                           (candidate.failureReason == "GNSS_TARGET_STALE" ||
                                            candidate.failureReason == "NO_CAUSAL_GNSS_TARGET");
        if (targetIsTemporarilyUnavailable)
        {
            if (_horizontalGnssWorldShadowStaleTargetGapStartTimestampNs <= 0L)
            {
                _horizontalGnssWorldShadowStaleTargetGapStartTimestampNs = targetTimestampNs;
                _horizontalGnssWorldShadowStaleTargetGapCount++;
            }

            _horizontalGnssWorldShadowStaleTargetGapDurationMilliseconds = System.Math.Max(
                0d,
                (targetTimestampNs - _horizontalGnssWorldShadowStaleTargetGapStartTimestampNs) / 1_000_000d);
        }
        else if (candidate.valid && _horizontalGnssWorldShadowStaleTargetGapStartTimestampNs > 0L)
        {
            _horizontalGnssWorldShadowStaleTargetGapDurationMilliseconds = System.Math.Max(
                _horizontalGnssWorldShadowStaleTargetGapDurationMilliseconds,
                (targetTimestampNs - _horizontalGnssWorldShadowStaleTargetGapStartTimestampNs) / 1_000_000d);
            _horizontalGnssWorldShadowStaleTargetGapStartTimestampNs = 0L;
        }

        HorizontalGnssWorldShadowDiagnostics = new HorizontalGnssWorldShadowOutageDiagnostics(
            active: _hasHorizontalGnssWorldShadowBaseline,
            productionRenderSource,
            boardHiddenAgeMilliseconds: _horizontalGnssWorldShadowOutageStartTimestampNs > 0L
                ? System.Math.Max(0d, (targetTimestampNs - _horizontalGnssWorldShadowOutageStartTimestampNs) / 1_000_000d)
                : double.NaN,
            productionRotationPropagationExpired: productionRenderSource == DjiRenderPoseSource.None,
            timeInNoneMilliseconds: _horizontalGnssWorldShadowNoneStartTimestampNs > 0L
                ? System.Math.Max(0d, (targetTimestampNs - _horizontalGnssWorldShadowNoneStartTimestampNs) / 1_000_000d)
                : 0d,
            targetGnssValid: candidate.valid,
            targetGnssAgeMilliseconds: candidate.gnssTargetAgeMilliseconds,
            candidateValidWhileProductionNone: productionRenderSource == DjiRenderPoseSource.None && candidate.valid,
            staleTargetGapCount: _horizontalGnssWorldShadowStaleTargetGapCount,
            staleTargetGapDurationMilliseconds: _horizontalGnssWorldShadowStaleTargetGapDurationMilliseconds,
            reacquisitionCandidateValid: LatestHorizontalGnssWorldShadowReacquisition.hasComparison,
            reacquisitionCandidateAgeMilliseconds: LatestHorizontalGnssWorldShadowReacquisition.candidateAgeMilliseconds,
            reacquisitionHorizontalResidualMeters: LatestHorizontalGnssWorldShadowReacquisition.navigationHorizontalErrorMagnitudeMeters);
    }

    private void EndHorizontalGnssWorldShadowOutage()
    {
        var diagnostics = HorizontalGnssWorldShadowDiagnostics;
        var smoothingReacquisition = LatestHorizontalGnssSmoothingReacquisition;
        ResetHorizontalGnssWorldShadowOutage();
        HorizontalGnssWorldShadowDiagnostics = diagnostics.WithActive(false);
        LatestHorizontalGnssSmoothingReacquisition = smoothingReacquisition;
    }

    private void RecordHorizontalGnssWorldShadowReacquisition(
        HorizontalGnssWorldShadowSnapshot candidate,
        Pose visualWorldFromCamera)
    {
        var visualWorldDelta = visualWorldFromCamera.position - _horizontalGnssWorldShadowWorldFromCameraBaseline.position;
        var visualNavigationDelta = Quaternion.Inverse(_worldFromNavigationRotation) * visualWorldDelta;
        var horizontalErrorInNavigation = new Vector2(
            visualNavigationDelta.x - candidate.navigationDelta.x,
            visualNavigationDelta.y - candidate.navigationDelta.y);
        LatestHorizontalGnssWorldShadowReacquisition = new HorizontalGnssWorldShadowReacquisitionSnapshot(
            true,
            candidate.visualBaselineTimestampNs,
            candidate.targetTimestampNs,
            candidate.candidateWorldFromCamera,
            visualWorldFromCamera,
            visualWorldFromCamera.position - candidate.candidateWorldFromCamera.position,
            horizontalErrorInNavigation,
            horizontalErrorInNavigation.magnitude,
            candidate.gnssTargetAgeMilliseconds);
        HorizontalGnssWorldShadowDiagnostics = HorizontalGnssWorldShadowDiagnostics.WithReacquisition(
            candidateValid: true,
            candidateAgeMilliseconds: candidate.gnssTargetAgeMilliseconds,
            horizontalResidualMeters: horizontalErrorInNavigation.magnitude);
    }

    private void RecordHorizontalGnssSmoothingReacquisition(
        HorizontalGnssWorldShadowSnapshot rawCandidate,
        HorizontalGnssSmoothingShadowSnapshot smoothedCandidate,
        Pose visualWorldFromCamera)
    {
        var visualWorldDelta = visualWorldFromCamera.position - _horizontalGnssWorldShadowWorldFromCameraBaseline.position;
        var visualNavigationDelta = Quaternion.Inverse(_worldFromNavigationRotation) * visualWorldDelta;
        var heldPositionResidual = new Vector2(visualNavigationDelta.x, visualNavigationDelta.y).magnitude;
        var rawResidual = rawCandidate.valid
            ? new Vector2(
                visualNavigationDelta.x - rawCandidate.navigationDelta.x,
                visualNavigationDelta.y - rawCandidate.navigationDelta.y).magnitude
            : float.NaN;
        var smoothedResidual = smoothedCandidate.estimate.hasEstimate
            ? new Vector2(
                visualNavigationDelta.x - smoothedCandidate.navigationDelta.x,
                visualNavigationDelta.y - smoothedCandidate.navigationDelta.y).magnitude
            : float.NaN;
        LatestHorizontalGnssSmoothingReacquisition = new HorizontalGnssSmoothingReacquisitionSnapshot(
            smoothedCandidate.estimate.hasEstimate,
            smoothedCandidate.visualBaselineTimestampNs,
            smoothedCandidate.targetTimestampNs,
            rawResidual,
            smoothedResidual,
            heldPositionResidual,
            smoothedCandidate.candidateWorldFromCamera,
            visualWorldFromCamera);
    }

    private static bool TryGetCausalPositionSample(
        IReadOnlyList<TelemetryAircraftPositionSample> history,
        long targetTimestampNs,
        out TelemetryAircraftPositionSample sample)
    {
        sample = default;
        if (history == null || targetTimestampNs <= 0L)
            return false;

        var found = false;
        for (var index = 0; index < history.Count; index++)
        {
            var candidate = history[index];
            if (candidate.timestampNs > targetTimestampNs)
                break;
            sample = candidate;
            found = true;
        }
        return found;
    }

    public static bool HasValidInitialWorldInvariant(Pose cameraFromBoard)
    {
        return HasValidWorldInvariant(Pose.identity, cameraFromBoard);
    }

    public static bool HasValidWorldInvariant(Pose worldFromBoard, Pose cameraFromBoard)
    {
        if (!IsFiniteRigidPose(worldFromBoard) || !IsFiniteRigidPose(cameraFromBoard))
            return false;

        var normalizedCameraFromBoard = new Pose(
            cameraFromBoard.position,
            Quaternion.Normalize(cameraFromBoard.rotation));
        var worldFromCamera = ReferenceFrameTransforms.PoseFromMatrix(
            ReferenceFrameTransforms.Compose(
                ReferenceFrameTransforms.WorldFromBoard(worldFromBoard),
                ReferenceFrameTransforms.MatrixFromPose(
                    ReferenceFrameTransforms.Invert(normalizedCameraFromBoard))));
        var recomposedWorldFromBoard = ReferenceFrameTransforms.Compose(
            ReferenceFrameTransforms.WorldFromCamera(worldFromCamera),
            ReferenceFrameTransforms.MatrixFromPose(normalizedCameraFromBoard));
        return MatricesApproximatelyEqual(
            recomposedWorldFromBoard,
            ReferenceFrameTransforms.WorldFromBoard(worldFromBoard));
    }

    private float LatestWorldVisualHistoryAgeMs
    {
        get
        {
            if (_authoritativeVisualHistory.Count == 0 ||
                _rotationPropagationVisualBaselineTimestampNs <= 0L)
            {
                return 0f;
            }

            var latestTimestampNs = _authoritativeVisualHistory[_authoritativeVisualHistory.Count - 1].sourceTimestampNs;
            return Mathf.Max(0f, (latestTimestampNs - _rotationPropagationVisualBaselineTimestampNs) / 1_000_000f);
        }
    }

    private void RecordAuthoritativeVisualSnapshot(long sourceTimestampNs)
    {
        var snapshot = _state.Snapshot;
        if (snapshot.lastVisualSourceTimestampNs != sourceTimestampNs)
            return;

        if (_authoritativeVisualHistory.Count > 0 &&
            sourceTimestampNs <= _authoritativeVisualHistory[_authoritativeVisualHistory.Count - 1].sourceTimestampNs)
        {
            return;
        }

        _authoritativeVisualHistory.Add(new AuthoritativeWorldVisualSnapshot(
            sourceTimestampNs,
            snapshot.worldFromCamera));
        if (_authoritativeVisualHistory.Count > AuthoritativeVisualHistoryCapacity)
            _authoritativeVisualHistory.RemoveAt(0);
    }

    private bool TryResolveAuthoritativeVisualSnapshot(
        long sourceTimestampNs,
        out AuthoritativeWorldVisualSnapshot resolvedSnapshot)
    {
        for (var index = _authoritativeVisualHistory.Count - 1; index >= 0; index--)
        {
            var candidate = _authoritativeVisualHistory[index];
            if (candidate.sourceTimestampNs == sourceTimestampNs)
            {
                resolvedSnapshot = candidate;
                return true;
            }
        }

        resolvedSnapshot = default;
        return false;
    }

    private readonly struct AuthoritativeWorldVisualSnapshot
    {
        public readonly long sourceTimestampNs;
        public readonly Pose worldFromCamera;

        public AuthoritativeWorldVisualSnapshot(
            long sourceTimestampNs,
            Pose worldFromCamera)
        {
            this.sourceTimestampNs = sourceTimestampNs;
            this.worldFromCamera = worldFromCamera;
        }
    }

    private static bool IsFiniteRigidPose(Pose pose)
    {
        if (!float.IsFinite(pose.position.x) ||
            !float.IsFinite(pose.position.y) ||
            !float.IsFinite(pose.position.z) ||
            !float.IsFinite(pose.rotation.x) ||
            !float.IsFinite(pose.rotation.y) ||
            !float.IsFinite(pose.rotation.z) ||
            !float.IsFinite(pose.rotation.w))
        {
            return false;
        }

        return pose.rotation.x * pose.rotation.x +
               pose.rotation.y * pose.rotation.y +
               pose.rotation.z * pose.rotation.z +
               pose.rotation.w * pose.rotation.w > 0.0000001f;
    }

    private static bool IsFiniteQuaternion(Quaternion rotation) =>
        float.IsFinite(rotation.x) &&
        float.IsFinite(rotation.y) &&
        float.IsFinite(rotation.z) &&
        float.IsFinite(rotation.w) &&
        rotation.x * rotation.x + rotation.y * rotation.y +
        rotation.z * rotation.z + rotation.w * rotation.w > 0.0000001f;

    private static bool MatricesApproximatelyEqual(Matrix4x4 first, Matrix4x4 second)
    {
        for (var row = 0; row < 4; row++)
        for (var column = 0; column < 4; column++)
        {
            if (Mathf.Abs(first[row, column] - second[row, column]) > TransformTolerance)
                return false;
        }

        return true;
    }
}

/// <summary>
/// Read-only horizontal-GNSS candidate for the persistent-world shadow. It is
/// diagnostic only: no render authority or WorldLocalizationState mutation is
/// permitted through this contract.
/// </summary>
public readonly struct HorizontalGnssWorldShadowSnapshot
{
    public const float DefaultMaximumAgeMilliseconds = 300f;

    public readonly bool valid;
    public readonly string failureReason;
    public readonly long visualBaselineTimestampNs;
    public readonly long gnssBaselineTimestampNs;
    public readonly double gnssBaselineAgeMilliseconds;
    public readonly long targetTimestampNs;
    public readonly long gnssTargetTimestampNs;
    public readonly double gnssTargetAgeMilliseconds;
    public readonly Vector3 navigationDelta;
    public readonly Vector3 worldDelta;
    public readonly Pose candidateWorldFromCamera;
    public readonly bool hasWorldFromNavigationRotation;
    public readonly Quaternion worldFromNavigationRotation;
    public readonly bool cameraLeverArmApplied;
    public readonly bool hasCandidateRotation;

    private HorizontalGnssWorldShadowSnapshot(
        bool valid,
        string failureReason,
        long visualBaselineTimestampNs,
        long gnssBaselineTimestampNs,
        double gnssBaselineAgeMilliseconds,
        long targetTimestampNs,
        long gnssTargetTimestampNs,
        double gnssTargetAgeMilliseconds,
        Vector3 navigationDelta,
        Vector3 worldDelta,
        Pose candidateWorldFromCamera,
        bool hasWorldFromNavigationRotation,
        Quaternion worldFromNavigationRotation,
        bool hasCandidateRotation)
    {
        this.valid = valid;
        this.failureReason = failureReason ?? string.Empty;
        this.visualBaselineTimestampNs = visualBaselineTimestampNs;
        this.gnssBaselineTimestampNs = gnssBaselineTimestampNs;
        this.gnssBaselineAgeMilliseconds = gnssBaselineAgeMilliseconds;
        this.targetTimestampNs = targetTimestampNs;
        this.gnssTargetTimestampNs = gnssTargetTimestampNs;
        this.gnssTargetAgeMilliseconds = gnssTargetAgeMilliseconds;
        this.navigationDelta = navigationDelta;
        this.worldDelta = worldDelta;
        this.candidateWorldFromCamera = candidateWorldFromCamera;
        this.hasWorldFromNavigationRotation = hasWorldFromNavigationRotation;
        this.worldFromNavigationRotation = worldFromNavigationRotation;
        cameraLeverArmApplied = false;
        this.hasCandidateRotation = hasCandidateRotation;
    }

    public static HorizontalGnssWorldShadowSnapshot AwaitingTarget(
        long visualBaselineTimestampNs,
        long gnssBaselineTimestampNs,
        double gnssBaselineAgeMilliseconds,
        Quaternion worldFromNavigationRotation) =>
        new(
            valid: false,
            failureReason: "AWAITING_GNSS_TARGET",
            visualBaselineTimestampNs,
            gnssBaselineTimestampNs,
            gnssBaselineAgeMilliseconds,
            targetTimestampNs: 0L,
            gnssTargetTimestampNs: 0L,
            gnssTargetAgeMilliseconds: double.PositiveInfinity,
            navigationDelta: Vector3.zero,
            worldDelta: Vector3.zero,
            candidateWorldFromCamera: Pose.identity,
            hasWorldFromNavigationRotation: true,
            worldFromNavigationRotation,
            hasCandidateRotation: false);

    public static HorizontalGnssWorldShadowSnapshot Valid(
        long visualBaselineTimestampNs,
        long gnssBaselineTimestampNs,
        double gnssBaselineAgeMilliseconds,
        long targetTimestampNs,
        long gnssTargetTimestampNs,
        double gnssTargetAgeMilliseconds,
        Vector3 navigationDelta,
        Vector3 worldDelta,
        Pose candidateWorldFromCamera,
        Quaternion worldFromNavigationRotation,
        bool hasCandidateRotation = true) =>
        new(
            valid: true,
            failureReason: string.Empty,
            visualBaselineTimestampNs,
            gnssBaselineTimestampNs,
            gnssBaselineAgeMilliseconds,
            targetTimestampNs,
            gnssTargetTimestampNs,
            gnssTargetAgeMilliseconds,
            navigationDelta,
            worldDelta,
            candidateWorldFromCamera,
            hasWorldFromNavigationRotation: true,
            worldFromNavigationRotation,
            hasCandidateRotation);

    public static HorizontalGnssWorldShadowSnapshot Invalid(
        string failureReason,
        long visualBaselineTimestampNs,
        long gnssBaselineTimestampNs = 0L,
        double gnssBaselineAgeMilliseconds = double.PositiveInfinity,
        long targetTimestampNs = 0L,
        long gnssTargetTimestampNs = 0L,
        double gnssTargetAgeMilliseconds = double.PositiveInfinity) =>
        new(
            valid: false,
            failureReason,
            visualBaselineTimestampNs,
            gnssBaselineTimestampNs,
            gnssBaselineAgeMilliseconds,
            targetTimestampNs,
            gnssTargetTimestampNs,
            gnssTargetAgeMilliseconds,
            navigationDelta: Vector3.zero,
            worldDelta: Vector3.zero,
            candidateWorldFromCamera: Pose.identity,
            hasWorldFromNavigationRotation: false,
            worldFromNavigationRotation: Quaternion.identity,
            hasCandidateRotation: false);
}

/// <summary>
/// Position-only residual measured when authoritative visual localization
/// returns. The navigation horizontal residual deliberately excludes all
/// unvalidated vertical and camera-lever-arm modeling.
/// </summary>
public readonly struct HorizontalGnssWorldShadowReacquisitionSnapshot
{
    public readonly bool hasComparison;
    public readonly long visualBaselineTimestampNs;
    public readonly long targetTimestampNs;
    public readonly Pose candidateWorldFromCamera;
    public readonly Pose visualWorldFromCamera;
    public readonly Vector3 worldPositionError;
    public readonly Vector2 navigationHorizontalError;
    public readonly float navigationHorizontalErrorMagnitudeMeters;
    public readonly double candidateAgeMilliseconds;

    public HorizontalGnssWorldShadowReacquisitionSnapshot(
        bool hasComparison,
        long visualBaselineTimestampNs,
        long targetTimestampNs,
        Pose candidateWorldFromCamera,
        Pose visualWorldFromCamera,
        Vector3 worldPositionError,
        Vector2 navigationHorizontalError,
        float navigationHorizontalErrorMagnitudeMeters,
        double candidateAgeMilliseconds = double.PositiveInfinity)
    {
        this.hasComparison = hasComparison;
        this.visualBaselineTimestampNs = visualBaselineTimestampNs;
        this.targetTimestampNs = targetTimestampNs;
        this.candidateWorldFromCamera = candidateWorldFromCamera;
        this.visualWorldFromCamera = visualWorldFromCamera;
        this.worldPositionError = worldPositionError;
        this.navigationHorizontalError = navigationHorizontalError;
        this.navigationHorizontalErrorMagnitudeMeters = navigationHorizontalErrorMagnitudeMeters;
        this.candidateAgeMilliseconds = candidateAgeMilliseconds;
    }
}

/// <summary>
/// Lifecycle metadata for the retained horizontal-only GNSS outage diagnostic.
/// It is observational data only and cannot mutate the persistent World state.
/// </summary>
public readonly struct HorizontalGnssWorldShadowOutageDiagnostics
{
    public readonly bool active;
    public readonly DjiRenderPoseSource productionRenderSource;
    public readonly double boardHiddenAgeMilliseconds;
    public readonly bool productionRotationPropagationExpired;
    public readonly double timeInNoneMilliseconds;
    public readonly bool targetGnssValid;
    public readonly double targetGnssAgeMilliseconds;
    public readonly bool candidateValidWhileProductionNone;
    public readonly int staleTargetGapCount;
    public readonly double staleTargetGapDurationMilliseconds;
    public readonly bool reacquisitionCandidateValid;
    public readonly double reacquisitionCandidateAgeMilliseconds;
    public readonly float reacquisitionHorizontalResidualMeters;

    public HorizontalGnssWorldShadowOutageDiagnostics(
        bool active,
        DjiRenderPoseSource productionRenderSource,
        double boardHiddenAgeMilliseconds,
        bool productionRotationPropagationExpired,
        double timeInNoneMilliseconds,
        bool targetGnssValid,
        double targetGnssAgeMilliseconds,
        bool candidateValidWhileProductionNone,
        int staleTargetGapCount,
        double staleTargetGapDurationMilliseconds,
        bool reacquisitionCandidateValid,
        double reacquisitionCandidateAgeMilliseconds,
        float reacquisitionHorizontalResidualMeters)
    {
        this.active = active;
        this.productionRenderSource = productionRenderSource;
        this.boardHiddenAgeMilliseconds = boardHiddenAgeMilliseconds;
        this.productionRotationPropagationExpired = productionRotationPropagationExpired;
        this.timeInNoneMilliseconds = timeInNoneMilliseconds;
        this.targetGnssValid = targetGnssValid;
        this.targetGnssAgeMilliseconds = targetGnssAgeMilliseconds;
        this.candidateValidWhileProductionNone = candidateValidWhileProductionNone;
        this.staleTargetGapCount = staleTargetGapCount;
        this.staleTargetGapDurationMilliseconds = staleTargetGapDurationMilliseconds;
        this.reacquisitionCandidateValid = reacquisitionCandidateValid;
        this.reacquisitionCandidateAgeMilliseconds = reacquisitionCandidateAgeMilliseconds;
        this.reacquisitionHorizontalResidualMeters = reacquisitionHorizontalResidualMeters;
    }

    public HorizontalGnssWorldShadowOutageDiagnostics WithActive(bool active) => new(
        active,
        productionRenderSource,
        boardHiddenAgeMilliseconds,
        productionRotationPropagationExpired,
        timeInNoneMilliseconds,
        targetGnssValid,
        targetGnssAgeMilliseconds,
        candidateValidWhileProductionNone,
        staleTargetGapCount,
        staleTargetGapDurationMilliseconds,
        reacquisitionCandidateValid,
        reacquisitionCandidateAgeMilliseconds,
        reacquisitionHorizontalResidualMeters);

    public HorizontalGnssWorldShadowOutageDiagnostics WithReacquisition(
        bool candidateValid,
        double candidateAgeMilliseconds,
        float horizontalResidualMeters) => new(
        active,
        productionRenderSource,
        boardHiddenAgeMilliseconds,
        productionRotationPropagationExpired,
        timeInNoneMilliseconds,
        targetGnssValid,
        targetGnssAgeMilliseconds,
        candidateValidWhileProductionNone,
        staleTargetGapCount,
        staleTargetGapDurationMilliseconds,
        candidateValid,
        candidateAgeMilliseconds,
        horizontalResidualMeters);
}

/// <summary>
/// Diagnostic-only render-rate smoothing of the existing raw GNSS horizontal
/// target. The candidate does not mutate the persistent world state.
/// </summary>
public readonly struct HorizontalGnssSmoothingShadowSnapshot
{
    public readonly bool valid;
    public readonly string failureReason;
    public readonly long visualBaselineTimestampNs;
    public readonly long targetTimestampNs;
    public readonly HorizontalGnssWorldShadowSnapshot rawGnssCandidate;
    public readonly HorizontalGnssSmoothingEstimate estimate;
    public readonly Vector3 navigationDelta;
    public readonly Vector3 worldDelta;
    public readonly Pose candidateWorldFromCamera;
    public readonly Quaternion worldFromNavigationRotation;
    public readonly bool hasCandidateRotation;
    public readonly float rawCandidateStepMagnitudeMeters;

    private HorizontalGnssSmoothingShadowSnapshot(
        bool valid,
        string failureReason,
        long visualBaselineTimestampNs,
        long targetTimestampNs,
        HorizontalGnssWorldShadowSnapshot rawGnssCandidate,
        HorizontalGnssSmoothingEstimate estimate,
        Vector3 navigationDelta,
        Vector3 worldDelta,
        Pose candidateWorldFromCamera,
        Quaternion worldFromNavigationRotation,
        bool hasCandidateRotation,
        float rawCandidateStepMagnitudeMeters)
    {
        this.valid = valid;
        this.failureReason = failureReason ?? string.Empty;
        this.visualBaselineTimestampNs = visualBaselineTimestampNs;
        this.targetTimestampNs = targetTimestampNs;
        this.rawGnssCandidate = rawGnssCandidate;
        this.estimate = estimate;
        this.navigationDelta = navigationDelta;
        this.worldDelta = worldDelta;
        this.candidateWorldFromCamera = candidateWorldFromCamera;
        this.worldFromNavigationRotation = worldFromNavigationRotation;
        this.hasCandidateRotation = hasCandidateRotation;
        this.rawCandidateStepMagnitudeMeters = rawCandidateStepMagnitudeMeters;
    }

    public static HorizontalGnssSmoothingShadowSnapshot AwaitingTarget(
        long visualBaselineTimestampNs,
        long gnssBaselineTimestampNs,
        double gnssBaselineAgeMilliseconds,
        Quaternion worldFromNavigationRotation,
        HorizontalGnssSmoothingEstimate estimate) =>
        new(
            false,
            "AWAITING_GNSS_TARGET",
            visualBaselineTimestampNs,
            0L,
            HorizontalGnssWorldShadowSnapshot.AwaitingTarget(
                visualBaselineTimestampNs,
                gnssBaselineTimestampNs,
                gnssBaselineAgeMilliseconds,
                worldFromNavigationRotation),
            estimate,
            Vector3.zero,
            Vector3.zero,
            Pose.identity,
            worldFromNavigationRotation,
            false,
            0f);

    public static HorizontalGnssSmoothingShadowSnapshot FromEstimate(
        long visualBaselineTimestampNs,
        long targetTimestampNs,
        HorizontalGnssWorldShadowSnapshot rawGnssCandidate,
        HorizontalGnssSmoothingEstimate estimate,
        Vector3 navigationDelta,
        Vector3 worldDelta,
        Pose candidateWorldFromCamera,
        Quaternion worldFromNavigationRotation,
        bool hasCandidateRotation,
        float rawCandidateStepMagnitudeMeters) =>
        new(
            estimate.valid,
            estimate.failureReason,
            visualBaselineTimestampNs,
            targetTimestampNs,
            rawGnssCandidate,
            estimate,
            navigationDelta,
            worldDelta,
            candidateWorldFromCamera,
            worldFromNavigationRotation,
            hasCandidateRotation,
            rawCandidateStepMagnitudeMeters);

    public static HorizontalGnssSmoothingShadowSnapshot Invalid(
        string failureReason,
        long targetTimestampNs,
        long visualBaselineTimestampNs) =>
        new(
            false,
            failureReason,
            visualBaselineTimestampNs,
            targetTimestampNs,
            default,
            HorizontalGnssSmoothingEstimate.Invalid(
                failureReason,
                targetTimestampNs,
                HorizontalGnssSmoothingEstimator.DefaultTauSeconds,
                HorizontalGnssSmoothingEstimator.DefaultMaximumCorrectionSpeedMetersPerSecond),
            Vector3.zero,
            Vector3.zero,
            Pose.identity,
            Quaternion.identity,
            false,
            0f);
}

public readonly struct HorizontalGnssSmoothingReacquisitionSnapshot
{
    public readonly bool hasComparison;
    public readonly long visualBaselineTimestampNs;
    public readonly long targetTimestampNs;
    public readonly float rawGnssHorizontalResidualMeters;
    public readonly float smoothedHorizontalResidualMeters;
    public readonly float heldPositionHorizontalResidualMeters;
    public readonly Pose smoothedCandidateWorldFromCamera;
    public readonly Pose visualWorldFromCamera;

    public HorizontalGnssSmoothingReacquisitionSnapshot(
        bool hasComparison,
        long visualBaselineTimestampNs,
        long targetTimestampNs,
        float rawGnssHorizontalResidualMeters,
        float smoothedHorizontalResidualMeters,
        float heldPositionHorizontalResidualMeters,
        Pose smoothedCandidateWorldFromCamera,
        Pose visualWorldFromCamera)
    {
        this.hasComparison = hasComparison;
        this.visualBaselineTimestampNs = visualBaselineTimestampNs;
        this.targetTimestampNs = targetTimestampNs;
        this.rawGnssHorizontalResidualMeters = rawGnssHorizontalResidualMeters;
        this.smoothedHorizontalResidualMeters = smoothedHorizontalResidualMeters;
        this.heldPositionHorizontalResidualMeters = heldPositionHorizontalResidualMeters;
        this.smoothedCandidateWorldFromCamera = smoothedCandidateWorldFromCamera;
        this.visualWorldFromCamera = visualWorldFromCamera;
    }
}

/// <summary>
/// DroneView-session component that observes authoritative visual localization
/// without taking ownership of the production board-vision or rendering paths.
/// </summary>
[DisallowMultipleComponent]
public sealed class DjiWorldLocalizationShadow : MonoBehaviour
{
    public event System.Action<HorizontalAdmissionTrace> HorizontalAdmissionEvaluated;
    private bool _admissionObserverFailureLogged;
    private DjiBoardVisionProvider _boardVisionProvider;
    private DjiCameraPoseProvider _telemetryProvider;
    private readonly DjiWorldLocalizationShadowCoordinator _coordinator = new();
    private float _nextRotationPropagationEquivalenceLogTime;

    public WorldLocalizationSnapshot Snapshot => _coordinator.Snapshot;
    public bool HasActiveRotationPropagation => _coordinator.HasActiveRotationPropagation;
    public WorldRotationPropagationEquivalenceSnapshot RotationPropagationEquivalence =>
        _coordinator.RotationPropagationEquivalence;
    public HorizontalGnssWorldShadowSnapshot HorizontalGnssWorldShadow =>
        _coordinator.LatestHorizontalGnssWorldShadow;
    public HorizontalGnssWorldShadowReacquisitionSnapshot HorizontalGnssWorldShadowReacquisition =>
        _coordinator.LatestHorizontalGnssWorldShadowReacquisition;
    public HorizontalGnssWorldShadowOutageDiagnostics HorizontalGnssWorldShadowDiagnostics =>
        _coordinator.HorizontalGnssWorldShadowDiagnostics;
    public HorizontalGnssSmoothingShadowSnapshot HorizontalGnssSmoothingShadow =>
        _coordinator.LatestHorizontalGnssSmoothingShadow;
    public HorizontalGnssSmoothingReacquisitionSnapshot HorizontalGnssSmoothingReacquisition =>
        _coordinator.LatestHorizontalGnssSmoothingReacquisition;
    public bool ProductionHorizontalGnssApplied => _coordinator.ProductionHorizontalGnssApplied;
    public HorizontalGnssSmoothingState ProductionHorizontalGnssState => _coordinator.ProductionHorizontalGnssState;
    public string ProductionHorizontalGnssReason => _coordinator.ProductionHorizontalGnssReason;
    public VerticalAltitudeProductionSnapshot ProductionVerticalAltitude => _coordinator.ProductionVerticalAltitude;
    public HorizontalGnssQualityAdmissionSnapshot HorizontalGnssQualityAdmission =>
        _coordinator.HorizontalGnssQualityAdmission;

    public void BeginRotationPropagation(
        Pose cameraFromBoardVisual,
        long visualBaselineTimestampNs,
        Quaternion navigationFromCameraBaseline)
    {
        var telemetryProvider = ResolveTelemetryProvider();
        if (!_coordinator.TryBeginRotationPropagation(
                cameraFromBoardVisual,
                visualBaselineTimestampNs,
                navigationFromCameraBaseline,
                telemetryProvider?.AircraftPositionHistory,
                HorizontalGnssWorldShadowSnapshot.DefaultMaximumAgeMilliseconds,
                out var rejectionReason))
        {
            Debug.LogWarning(
                "WORLD_LOCALIZATION_SHADOW_ROTATION_START_REJECTED " +
                $"reason={rejectionReason} " +
                $"productionBaselineTimestampNs={_coordinator.LastProductionBaselineTimestampNs} " +
                $"resolvedWorldBaselineTimestampNs={_coordinator.LastResolvedWorldBaselineTimestampNs} " +
                $"historicalBaselineResolved={_coordinator.LastHistoricalBaselineResolved} " +
                $"worldBaselineHistoryCount={_coordinator.AuthoritativeVisualHistoryCount} " +
                $"baselineResolveFailureReason={_coordinator.LastBaselineResolveFailureReason}");
            return;
        }
    }

    public void UpdateRotationPropagation(
        long telemetryTimestampNs,
        Quaternion navigationFromCameraTarget,
        Pose productionCameraFromBoard)
    {
        UpdateRotationPropagation(telemetryTimestampNs, telemetryTimestampNs,
            navigationFromCameraTarget, productionCameraFromBoard);
    }

    public void UpdateRotationPropagation(
        long evaluationTimestampNs,
        long attitudeMeasurementTimestampNs,
        Quaternion navigationFromCameraTarget,
        Pose productionCameraFromBoard)
    {
        var previousHorizontalEstimate = _coordinator.LatestHorizontalGnssSmoothingShadow.estimate;
        if (!_coordinator.TryEvaluateRotationPropagation(
                evaluationTimestampNs,
                attitudeMeasurementTimestampNs,
                navigationFromCameraTarget,
                productionCameraFromBoard,
                ResolveTelemetryProvider()?.AircraftPositionHistory,
                HorizontalGnssWorldShadowSnapshot.DefaultMaximumAgeMilliseconds,
                out var rejectionReason))
        {
            if (rejectionReason != "PROPAGATION_NOT_ACTIVE" &&
                rejectionReason != "NON_INCREASING_PROPAGATION_TIMESTAMP")
                Debug.LogWarning($"WORLD_LOCALIZATION_SHADOW_ROTATION_UPDATE_REJECTED reason={rejectionReason}");
            return;
        }

        try
        {
            HorizontalAdmissionEvaluated?.Invoke(new HorizontalAdmissionTrace(
                evaluationTimestampNs, previousHorizontalEstimate,
                _coordinator.LatestHorizontalGnssSmoothingShadow, _coordinator.HorizontalGnssQualityAdmission,
                _coordinator.PropagationAttitudeTimestampNs, _coordinator.PropagationAttitudeAgeMilliseconds,
                _coordinator.ProductionVerticalAltitude, _coordinator.LastProductionBaselineTimestampNs));
        }
        catch (System.Exception exception)
        {
            // An observer failure must not interrupt the already-computed pose.
            if (!_admissionObserverFailureLogged)
                Debug.LogWarning($"DJI_MOTION_DIAGNOSTIC_OBSERVER_FAILED reason={exception.Message}");
            _admissionObserverFailureLogged = true;
        }

        if (Time.realtimeSinceStartup < _nextRotationPropagationEquivalenceLogTime)
            return;

        _nextRotationPropagationEquivalenceLogTime = Time.realtimeSinceStartup + 1f;
        var diagnostic = _coordinator.RotationPropagationEquivalence;
        Debug.Log(
            "WORLD_LOCALIZATION_SHADOW_ROTATION_EQUIVALENCE " +
            $"telemetryTimestampNs={diagnostic.telemetryTimestampNs} " +
            $"evaluationTimestampNs={_coordinator.PropagationEvaluationTimestampNs} " +
            $"attitudeTimestampNs={_coordinator.PropagationAttitudeTimestampNs} " +
            $"attitudeAgeMs={_coordinator.PropagationAttitudeAgeMilliseconds:F1} " +
            $"gnssAgeMs={_coordinator.LatestHorizontalGnssWorldShadow.gnssTargetAgeMilliseconds:F1} " +
            $"horizontalDelta={_coordinator.LatestHorizontalGnssSmoothingShadow.navigationDelta} " +
            $"translationResidualM={diagnostic.latestTranslationResidualM:F6} " +
            $"rotationResidualDeg={diagnostic.latestRotationResidualDeg:F6} " +
            $"maxTranslationResidualM={diagnostic.maxTranslationResidualM:F6} " +
            $"maxRotationResidualDeg={diagnostic.maxRotationResidualDeg:F6} " +
            $"matchedSamples={diagnostic.sampleCount}");
    }

    public void StopRotationPropagation() => _coordinator.StopRotationPropagation();

    public void ResetHorizontalGnssWorldShadowOutage() => _coordinator.ResetHorizontalGnssWorldShadowOutage();

    public void Configure(DjiBoardVisionProvider boardVisionProvider)
    {
        if (_boardVisionProvider == boardVisionProvider)
            return;

        Unsubscribe();
        _boardVisionProvider = boardVisionProvider;
        _telemetryProvider = FindFirstObjectByType<DjiCameraPoseProvider>();
        Subscribe();
    }

    private void OnEnable() => Subscribe();

    private void OnDisable()
    {
        _coordinator.ResetHorizontalGnssWorldShadowOutage();
        _coordinator.ResetHorizontalGnssQualityAdmission();
        Unsubscribe();
    }

    private void Update()
    {
        var telemetryProvider = ResolveTelemetryProvider();
        _coordinator.ObserveHorizontalGnssQuality(
            telemetryProvider?.LatestGpsSatelliteCount,
            telemetryProvider?.NewestAircraftPositionTimestampNs ?? 0L);

        if (_boardVisionProvider == null)
            return;

        var nowTimestampNs = DjiBoardVisionBridge.GetMonotonicTimestampNs();
        if (!_coordinator.HasActiveHorizontalGnssWorldShadow)
            return;

        _coordinator.UpdateHorizontalGnssWorldShadowDuringBoardHidden(
            nowTimestampNs,
            _boardVisionProvider.CurrentRenderPoseSource,
            telemetryProvider?.AircraftPositionHistory,
            HorizontalGnssWorldShadowSnapshot.DefaultMaximumAgeMilliseconds);
    }

    private void Subscribe()
    {
        if (_boardVisionProvider != null)
            _boardVisionProvider.AuthoritativeVisualPoseAccepted += OnAuthoritativeVisualPoseAccepted;
    }

    private void Unsubscribe()
    {
        if (_boardVisionProvider != null)
            _boardVisionProvider.AuthoritativeVisualPoseAccepted -= OnAuthoritativeVisualPoseAccepted;
    }

    private void OnAuthoritativeVisualPoseAccepted(Pose cameraFromBoard, long sourceTimestampNs)
    {
        // Upstream acceptance can still be followed by another pending/failed
        // candidate before the renderer selects VISUAL. Keep the live outage
        // and both position estimators until that final authority decision.
        if (_coordinator.HasActiveRotationPropagation)
            return;

        TryCommitVisualRenderAuthority(cameraFromBoard, sourceTimestampNs, out _);
    }

    /// <summary>
    /// Called only after the render resolver has selected the accepted visual
    /// pose. This is the commit point that may end live world propagation.
    /// </summary>
    public bool TryCommitVisualRenderAuthority(Pose cameraFromBoard, long sourceTimestampNs, out string rejectionReason)
    {
        var current = _coordinator.Snapshot;
        if (current.phase == WorldLocalizationPhase.VisualLocalized &&
            current.lastVisualSourceTimestampNs == sourceTimestampNs)
        {
            rejectionReason = string.Empty;
            return true;
        }

        var wasUninitialized = !_coordinator.Snapshot.hasWorld;
        bool accepted;
        if (wasUninitialized)
        {
            accepted = _coordinator.TryInitializeFromVisualMeasurement(
                cameraFromBoard,
                sourceTimestampNs,
                isAuthoritativeVisual: true,
                out rejectionReason);
        }
        else
        {
            accepted = _coordinator.TryUpdateFromVisualMeasurement(
                cameraFromBoard,
                sourceTimestampNs,
                true,
                ResolveTelemetryProvider()?.AircraftPositionHistory,
                HorizontalGnssWorldShadowSnapshot.DefaultMaximumAgeMilliseconds,
                out rejectionReason);
        }
        if (!accepted)
        {
            if (wasUninitialized)
                Debug.LogError($"WORLD_LOCALIZATION_SHADOW_INITIALIZATION_REJECTED reason={rejectionReason}");
            return false;
        }

        if (wasUninitialized)
        {
            var snapshot = _coordinator.Snapshot;
            Debug.Log(
                "WORLD_LOCALIZATION_SHADOW_INITIALIZED " +
                $"sourceTimestampNs={sourceTimestampNs} " +
                $"cameraFromBoardPosition={cameraFromBoard.position} " +
                $"cameraFromBoardRotation={cameraFromBoard.rotation.eulerAngles} " +
                $"worldFromCameraPosition={snapshot.worldFromCamera.position} " +
                $"worldFromCameraRotation={snapshot.worldFromCamera.rotation.eulerAngles} " +
                "invariant=PASS");
        }
        return true;
    }

    private DjiCameraPoseProvider ResolveTelemetryProvider()
    {
        _telemetryProvider ??= FindFirstObjectByType<DjiCameraPoseProvider>();
        return _telemetryProvider;
    }
}
