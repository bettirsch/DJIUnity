using UnityEngine;

public enum DjiRenderPoseSource
{
    None,
    Visual,
    TelemetryPropagated = 3
}

/// <summary>
/// Selects whether the experimental horizontal GNSS contribution remains a
/// diagnostic or can augment the already-selected rotation propagated pose.
/// It never creates another render-pose source.
/// </summary>
public enum HorizontalTranslationRenderMode
{
    ShadowOnly,
    GuardedGnssRendered
}

public enum GuardedHorizontalTranslationState
{
    Off,
    Arming,
    Active,
    GnssGrace,
    Deactivating
}

[System.Serializable]
public sealed class GuardedHorizontalRenderConfiguration
{
    [Min(1f)] public float minPropagationAgeMilliseconds = 500f;
    [Min(1f)] public float maxPropagationAgeMilliseconds = 2000f;
    [Min(1f)] public float maxGnssAgeMilliseconds = 300f;
    // Wait long enough for the next distinct GNSS callback without weakening
    // the freshness requirement of either individual observation.
    [Min(1f)] public float armingTimeoutMilliseconds = 600f;
    [Min(0.001f)] public float horizontalEnableThresholdMeters = 0.25f;
    [Min(0.001f)] public float horizontalDisableThresholdMeters = 0.15f;
    [Min(1)] public int requiredConsistentGnssSamples = 2;
    [Range(0f, 180f)] public float maxConsistencyDirectionDifferenceDegrees = 45f;
    [Min(0f)] public float activationBlendMilliseconds = 300f;
    [Min(0f)] public float gnssLossGraceMilliseconds = 300f;
    [Min(0f)] public float deactivationBlendMilliseconds = 300f;
}

public readonly struct GuardedHorizontalObservation
{
    public readonly long nowTimestampNs;
    public readonly long gnssTimestampNs;
    public readonly float propagationAgeMilliseconds;
    public readonly bool hasFreshGnss;
    public readonly Vector2 horizontalCorrectionInNavigation;
    public readonly float gnssAgeMilliseconds;

    public GuardedHorizontalObservation(
        long nowTimestampNs,
        long gnssTimestampNs,
        float propagationAgeMilliseconds,
        bool hasFreshGnss,
        Vector2 horizontalCorrectionInNavigation,
        float gnssAgeMilliseconds)
    {
        this.nowTimestampNs = nowTimestampNs;
        this.gnssTimestampNs = gnssTimestampNs;
        this.propagationAgeMilliseconds = propagationAgeMilliseconds;
        this.hasFreshGnss = hasFreshGnss;
        this.horizontalCorrectionInNavigation = horizontalCorrectionInNavigation;
        this.gnssAgeMilliseconds = gnssAgeMilliseconds;
    }
}

public readonly struct GuardedHorizontalStateSnapshot
{
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
    public readonly string reason;

    public GuardedHorizontalStateSnapshot(
        GuardedHorizontalTranslationState state,
        bool eligible,
        bool active,
        Vector2 correctionInNavigation,
        Vector2 appliedCorrectionInNavigation,
        float blendAlpha,
        float gnssAgeMilliseconds,
        float propagationAgeMilliseconds,
        int consistentSampleCount,
        float directionDifferenceDegrees,
        float armingAgeMilliseconds,
        float armingTimeoutMilliseconds,
        long firstQualifyingSampleTimestampNs,
        long lastDistinctGnssTimestampNs,
        string reason)
    {
        this.state = state;
        this.eligible = eligible;
        this.active = active;
        this.correctionInNavigation = correctionInNavigation;
        this.appliedCorrectionInNavigation = appliedCorrectionInNavigation;
        this.blendAlpha = blendAlpha;
        this.gnssAgeMilliseconds = gnssAgeMilliseconds;
        this.propagationAgeMilliseconds = propagationAgeMilliseconds;
        this.consistentSampleCount = consistentSampleCount;
        this.directionDifferenceDegrees = directionDifferenceDegrees;
        this.armingAgeMilliseconds = armingAgeMilliseconds;
        this.armingTimeoutMilliseconds = armingTimeoutMilliseconds;
        this.firstQualifyingSampleTimestampNs = firstQualifyingSampleTimestampNs;
        this.lastDistinctGnssTimestampNs = lastDistinctGnssTimestampNs;
        this.reason = reason;
    }
}

/// <summary>
/// Pure, time-stamped policy for the experimental horizontal GNSS render
/// contribution. The caller owns the existing GNSS coordinate transform and
/// applies only the returned N/E contribution to its rotation-only pose.
/// </summary>
public sealed class GuardedHorizontalRenderStateMachine
{
    private GuardedHorizontalTranslationState _state;
    private Vector2 _lastTrustedCorrection;
    private Vector2 _activationCorrection;
    private Vector2 _deactivationCorrection;
    private long _activationStartTimestampNs;
    private long _graceStartTimestampNs;
    private long _deactivationStartTimestampNs;
    private long _armingFirstSampleTimestampNs;
    private long _lastDistinctGnssTimestampNs;
    private long _minimumAcceptedGnssTimestampNs;
    private int _consistentSampleCount;
    private float _lastDirectionDifferenceDegrees = float.NaN;

    public GuardedHorizontalStateSnapshot Update(
        HorizontalTranslationRenderMode mode,
        GuardedHorizontalRenderConfiguration configuration,
        GuardedHorizontalObservation observation)
    {
        if (mode != HorizontalTranslationRenderMode.GuardedGnssRendered)
            return Reset(observation, "SHADOW_ONLY");

        var eligibleAge = observation.propagationAgeMilliseconds >= configuration.minPropagationAgeMilliseconds &&
                          observation.propagationAgeMilliseconds <= configuration.maxPropagationAgeMilliseconds;
        if (!eligibleAge)
            return Reset(observation, observation.propagationAgeMilliseconds < configuration.minPropagationAgeMilliseconds
                ? "PROPAGATION_AGE_BELOW_MINIMUM"
                : "PROPAGATION_AGE_EXPIRED");

        var isNewGnssCallback = observation.gnssTimestampNs > _minimumAcceptedGnssTimestampNs &&
                                observation.gnssTimestampNs > _lastDistinctGnssTimestampNs;
        var isNewFreshObservation = isNewGnssCallback && observation.hasFreshGnss;
        var magnitude = observation.horizontalCorrectionInNavigation.magnitude;
        if (isNewGnssCallback)
        {
            _lastDistinctGnssTimestampNs = observation.gnssTimestampNs;
            if (observation.hasFreshGnss)
                _lastTrustedCorrection = observation.horizontalCorrectionInNavigation;
        }

        // Rendering polls can repeat a callback after it has aged past the
        // freshness bound. They are not observations and must not erase a
        // valid arming sample. Only distinct callbacks advance or reset it.
        string reason = null;
        var armingAgeBeforeUpdateMilliseconds = GetArmingAgeMilliseconds(observation.nowTimestampNs);
        var firstArmingSampleBeforeUpdateTimestampNs = _armingFirstSampleTimestampNs;
        switch (_state)
        {
            case GuardedHorizontalTranslationState.Off:
                if (isNewFreshObservation && magnitude >= configuration.horizontalEnableThresholdMeters)
                    BeginArming(observation);
                break;

            case GuardedHorizontalTranslationState.Arming:
                if (ElapsedMilliseconds(observation.nowTimestampNs, _armingFirstSampleTimestampNs) > configuration.armingTimeoutMilliseconds)
                {
                    ClearArming();
                    _state = GuardedHorizontalTranslationState.Off;
                    reason = "ARMING_TIMEOUT";
                    break;
                }

                if (isNewGnssCallback)
                {
                    if (!observation.hasFreshGnss)
                    {
                        ClearArming();
                        _state = GuardedHorizontalTranslationState.Off;
                        reason = "STALE_GNSS_CALLBACK";
                    }
                    else if (magnitude < configuration.horizontalEnableThresholdMeters)
                    {
                        ClearArming();
                        _state = GuardedHorizontalTranslationState.Off;
                        reason = "CORRECTION_BELOW_ENABLE_THRESHOLD";
                    }
                    else
                    {
                        ObserveArmingSample(observation, configuration);
                    }
                }
                break;

            case GuardedHorizontalTranslationState.Active:
                if (!observation.hasFreshGnss)
                {
                    _state = GuardedHorizontalTranslationState.GnssGrace;
                    _graceStartTimestampNs = observation.nowTimestampNs;
                }
                else if (isNewFreshObservation && magnitude <= configuration.horizontalDisableThresholdMeters)
                {
                    BeginDeactivation(observation.nowTimestampNs);
                }
                break;

            case GuardedHorizontalTranslationState.GnssGrace:
                if (isNewFreshObservation)
                {
                    if (magnitude <= configuration.horizontalDisableThresholdMeters)
                        BeginDeactivation(observation.nowTimestampNs);
                    else
                        _state = GuardedHorizontalTranslationState.Active;
                }
                else if (ElapsedMilliseconds(observation.nowTimestampNs, _graceStartTimestampNs) > configuration.gnssLossGraceMilliseconds)
                {
                    BeginDeactivation(observation.nowTimestampNs);
                }
                break;

            case GuardedHorizontalTranslationState.Deactivating:
                if (isNewFreshObservation && magnitude >= configuration.horizontalEnableThresholdMeters)
                {
                    _state = GuardedHorizontalTranslationState.Off;
                    ClearArming();
                    BeginArming(observation);
                }
                else if (ElapsedMilliseconds(observation.nowTimestampNs, _deactivationStartTimestampNs) >= configuration.deactivationBlendMilliseconds)
                {
                    _state = GuardedHorizontalTranslationState.Off;
                    ClearArming();
                    _lastTrustedCorrection = Vector2.zero;
                }
                break;
        }

        var alpha = ComputeBlendAlpha(configuration, observation.nowTimestampNs);
        var applied = _state == GuardedHorizontalTranslationState.Deactivating
            ? _deactivationCorrection * alpha
            : _lastTrustedCorrection * alpha;
        var active = (_state == GuardedHorizontalTranslationState.Active ||
                      _state == GuardedHorizontalTranslationState.GnssGrace) ||
            (_state == GuardedHorizontalTranslationState.Deactivating && alpha > 0f);
        var timedOutWhileArming = reason == "ARMING_TIMEOUT";
        return new GuardedHorizontalStateSnapshot(
            _state,
            eligibleAge && observation.hasFreshGnss,
            active,
            _lastTrustedCorrection,
            applied,
            alpha,
            observation.gnssAgeMilliseconds,
            observation.propagationAgeMilliseconds,
            _consistentSampleCount,
            _lastDirectionDifferenceDegrees,
            timedOutWhileArming ? armingAgeBeforeUpdateMilliseconds : GetArmingAgeMilliseconds(observation.nowTimestampNs),
            configuration.armingTimeoutMilliseconds,
            timedOutWhileArming ? firstArmingSampleBeforeUpdateTimestampNs : _armingFirstSampleTimestampNs,
            _lastDistinctGnssTimestampNs,
            reason ?? GetReason());
    }

    public GuardedHorizontalStateSnapshot Reset(GuardedHorizontalObservation observation, string reason)
    {
        _state = GuardedHorizontalTranslationState.Off;
        _lastTrustedCorrection = Vector2.zero;
        _activationCorrection = Vector2.zero;
        _deactivationCorrection = Vector2.zero;
        _activationStartTimestampNs = 0L;
        _graceStartTimestampNs = 0L;
        _deactivationStartTimestampNs = 0L;
        _armingFirstSampleTimestampNs = 0L;
        _lastDistinctGnssTimestampNs = 0L;
        // A reset starts a new propagation or A/B session. Cached callbacks
        // from before that boundary must never become its first sample.
        _minimumAcceptedGnssTimestampNs = System.Math.Max(
            _minimumAcceptedGnssTimestampNs,
            observation.nowTimestampNs);
        _consistentSampleCount = 0;
        _lastDirectionDifferenceDegrees = float.NaN;
        return new GuardedHorizontalStateSnapshot(
            _state, false, false, Vector2.zero, Vector2.zero, 0f,
            observation.gnssAgeMilliseconds, observation.propagationAgeMilliseconds,
            0, float.NaN, 0f, 0f, 0L, 0L, reason);
    }

    private void BeginArming(GuardedHorizontalObservation observation)
    {
        _state = GuardedHorizontalTranslationState.Arming;
        _activationCorrection = observation.horizontalCorrectionInNavigation;
        _lastTrustedCorrection = observation.horizontalCorrectionInNavigation;
        _armingFirstSampleTimestampNs = observation.nowTimestampNs;
        _consistentSampleCount = 1;
        _lastDirectionDifferenceDegrees = float.NaN;
    }

    private void ObserveArmingSample(GuardedHorizontalObservation observation, GuardedHorizontalRenderConfiguration configuration)
    {
        _lastDirectionDifferenceDegrees = Vector2.Angle(_activationCorrection, observation.horizontalCorrectionInNavigation);
        if (_lastDirectionDifferenceDegrees > configuration.maxConsistencyDirectionDifferenceDegrees)
        {
            _activationCorrection = observation.horizontalCorrectionInNavigation;
            _lastTrustedCorrection = observation.horizontalCorrectionInNavigation;
            _consistentSampleCount = 1;
            return;
        }

        _lastTrustedCorrection = observation.horizontalCorrectionInNavigation;
        _consistentSampleCount++;
        if (_consistentSampleCount >= configuration.requiredConsistentGnssSamples)
        {
            _state = GuardedHorizontalTranslationState.Active;
            _activationStartTimestampNs = observation.nowTimestampNs;
        }
    }

    private void BeginDeactivation(long nowTimestampNs)
    {
        _state = GuardedHorizontalTranslationState.Deactivating;
        _deactivationCorrection = _lastTrustedCorrection;
        _deactivationStartTimestampNs = nowTimestampNs;
        ClearArming();
    }

    private void ClearArming()
    {
        _activationCorrection = Vector2.zero;
        _armingFirstSampleTimestampNs = 0L;
        _consistentSampleCount = 0;
        _lastDirectionDifferenceDegrees = float.NaN;
    }

    private float ComputeBlendAlpha(GuardedHorizontalRenderConfiguration configuration, long nowTimestampNs)
    {
        if (_state == GuardedHorizontalTranslationState.Arming || _state == GuardedHorizontalTranslationState.Off)
            return 0f;
        if (_state == GuardedHorizontalTranslationState.Deactivating)
            return configuration.deactivationBlendMilliseconds <= 0f
                ? 0f
                : 1f - Mathf.Clamp01(ElapsedMilliseconds(nowTimestampNs, _deactivationStartTimestampNs) / configuration.deactivationBlendMilliseconds);
        if (_activationStartTimestampNs <= 0L || configuration.activationBlendMilliseconds <= 0f)
            return 1f;
        return Mathf.Clamp01(ElapsedMilliseconds(nowTimestampNs, _activationStartTimestampNs) / configuration.activationBlendMilliseconds);
    }

    private string GetReason() => _state switch
    {
        GuardedHorizontalTranslationState.Arming => "AWAITING_CONSISTENT_GNSS",
        GuardedHorizontalTranslationState.Active => "FRESH_GNSS_ACTIVE",
        GuardedHorizontalTranslationState.GnssGrace => "GNSS_TEMPORARILY_STALE",
        GuardedHorizontalTranslationState.Deactivating => "SMOOTH_DEACTIVATION",
        _ => "OFF"
    };

    private float GetArmingAgeMilliseconds(long nowTimestampNs) =>
        _state == GuardedHorizontalTranslationState.Arming
            ? ElapsedMilliseconds(nowTimestampNs, _armingFirstSampleTimestampNs)
            : 0f;

    private static float ElapsedMilliseconds(long nowTimestampNs, long startTimestampNs) =>
        nowTimestampNs > startTimestampNs && startTimestampNs > 0L
            ? (float)((nowTimestampNs - startTimestampNs) / 1_000_000.0)
            : 0f;
}

public enum HybridHorizontalBestSource
{
    Unavailable,
    Similar,
    RotationOnly,
    FreshVelocity,
    Gnss,
    Hybrid
}

/// <summary>
/// Pure policy for selecting the one CameraFromBoard pose consumed by the
/// DJI overlay. It deliberately has no rendering or telemetry dependencies.
/// </summary>
public static class RotationPropagationRenderPolicy
{
    public static HybridHorizontalBestSource SelectBestHorizontalSource(
        float rotationOnlyErrorMeters,
        float freshVelocityErrorMeters,
        float gnssErrorMeters,
        float hybridErrorMeters,
        float neutralMarginMeters)
    {
        var minimum = float.PositiveInfinity;
        var finiteCount = 0;
        foreach (var error in new[]
                 {
                     rotationOnlyErrorMeters,
                     freshVelocityErrorMeters,
                     gnssErrorMeters,
                     hybridErrorMeters
                 })
        {
            if (!float.IsFinite(error))
                continue;

            minimum = Mathf.Min(minimum, error);
            finiteCount++;
        }

        if (finiteCount == 0)
            return HybridHorizontalBestSource.Unavailable;

        var nearBestCount = 0;
        if (float.IsFinite(rotationOnlyErrorMeters) && rotationOnlyErrorMeters <= minimum + neutralMarginMeters)
            nearBestCount++;
        if (float.IsFinite(freshVelocityErrorMeters) && freshVelocityErrorMeters <= minimum + neutralMarginMeters)
            nearBestCount++;
        if (float.IsFinite(gnssErrorMeters) && gnssErrorMeters <= minimum + neutralMarginMeters)
            nearBestCount++;
        if (float.IsFinite(hybridErrorMeters) && hybridErrorMeters <= minimum + neutralMarginMeters)
            nearBestCount++;
        if (nearBestCount > 1)
            return HybridHorizontalBestSource.Similar;

        if (rotationOnlyErrorMeters <= minimum + neutralMarginMeters)
            return HybridHorizontalBestSource.RotationOnly;
        if (freshVelocityErrorMeters <= minimum + neutralMarginMeters)
            return HybridHorizontalBestSource.FreshVelocity;
        if (gnssErrorMeters <= minimum + neutralMarginMeters)
            return HybridHorizontalBestSource.Gnss;
        return HybridHorizontalBestSource.Hybrid;
    }

    /// <summary>
    /// Evaluates whether a newly reacquired visual pose is stable enough to
    /// take render authority back from the rotation-only prediction. This
    /// deliberately compares poses only; it does not blend or modify either.
    /// </summary>
    public static bool IsVisualReacquisitionReady(
        bool hasHighConfidenceVisualPose,
        long reacquisitionStartTimestampNs,
        long candidateTimestampNs,
        float requiredDwellSeconds,
        Pose propagatedCameraFromBoard,
        Pose visualCameraFromBoard,
        float maximumPositionDifferenceMeters,
        float maximumRotationDifferenceDegrees,
        out float positionDifferenceMeters,
        out float rotationDifferenceDegrees)
    {
        positionDifferenceMeters = Vector3.Distance(
            propagatedCameraFromBoard.position,
            visualCameraFromBoard.position);
        rotationDifferenceDegrees = Quaternion.Angle(
            propagatedCameraFromBoard.rotation,
            visualCameraFromBoard.rotation);

        if (!hasHighConfidenceVisualPose ||
            reacquisitionStartTimestampNs <= RotationPropagationTiming.NoTimestamp ||
            candidateTimestampNs < reacquisitionStartTimestampNs ||
            !float.IsFinite(positionDifferenceMeters) ||
            !float.IsFinite(rotationDifferenceDegrees))
        {
            return false;
        }

        var dwellSeconds = (float)((candidateTimestampNs - reacquisitionStartTimestampNs) / 1_000_000_000.0);
        return dwellSeconds >= requiredDwellSeconds &&
               positionDifferenceMeters <= maximumPositionDifferenceMeters &&
               rotationDifferenceDegrees <= maximumRotationDifferenceDegrees;
    }

    /// <summary>
    /// A propagation timeout ends the old localization session. A fresh visual
    /// relocalization still needs stable high-quality frames, but must not be
    /// compared to a spatial prediction that has already expired.
    /// </summary>
    public static bool IsFreshVisualRelocalizationReady(
        bool hasHighConfidenceVisualPose,
        long relocalizationStartTimestampNs,
        long candidateTimestampNs,
        float requiredDwellSeconds)
    {
        if (!hasHighConfidenceVisualPose ||
            relocalizationStartTimestampNs <= RotationPropagationTiming.NoTimestamp ||
            candidateTimestampNs < relocalizationStartTimestampNs)
        {
            return false;
        }

        var dwellSeconds = (float)((candidateTimestampNs - relocalizationStartTimestampNs) / 1_000_000_000.0);
        return dwellSeconds >= requiredDwellSeconds;
    }

    /// <summary>
    /// A long-lived telemetry estimate is allowed to drift. Its visual return
    /// remains quality-and-dwell gated, but is not compared with the old
    /// short-window rotation-only prediction.
    /// </summary>
    public static bool IsLongLivedTelemetryReacquisitionReady(
        bool hasHighConfidenceVisualPose,
        long reacquisitionStartTimestampNs,
        long candidateTimestampNs,
        float requiredDwellSeconds) =>
        IsFreshVisualRelocalizationReady(
            hasHighConfidenceVisualPose,
            reacquisitionStartTimestampNs,
            candidateTimestampNs,
            requiredDwellSeconds);

    /// <summary>
    /// Telemetry paired with a provisional visual candidate is diagnostic-only
    /// until the renderer has actually handed authority back to VISUAL.
    /// </summary>
    public static bool ShouldPreservePropagationForProvisionalVisual(
        DjiRenderPoseSource currentRenderPoseSource) =>
        currentRenderPoseSource == DjiRenderPoseSource.TelemetryPropagated;

    public static bool IsWithinShortPropagationWindow(float ageSeconds, float shortWindowSeconds) =>
        ageSeconds <= shortWindowSeconds;

    public static bool IsEnabledAllowed(
        DjiBoardVisionProvider.RotationPropagationMode mode,
        bool safeForRotationOnlyPropagation,
        bool developerOverride) =>
        mode == DjiBoardVisionProvider.RotationPropagationMode.Enabled &&
        (safeForRotationOnlyPropagation || developerOverride);

    public static DjiRenderPoseSource Resolve(
        bool hasVisualPose,
        float visualPoseAgeSeconds,
        float visualPoseFreshnessSeconds,
        bool rotationPropagationEnabled,
        bool hasPredictedPose,
        float propagationDurationSeconds,
        float maximumPropagationDurationSeconds,
        bool hasLongLivedTelemetryLocalization = false)
    {
        if (hasVisualPose && visualPoseAgeSeconds <= visualPoseFreshnessSeconds)
            return DjiRenderPoseSource.Visual;

        if (rotationPropagationEnabled && hasPredictedPose &&
            (IsWithinShortPropagationWindow(propagationDurationSeconds, maximumPropagationDurationSeconds) ||
             hasLongLivedTelemetryLocalization))
            return DjiRenderPoseSource.TelemetryPropagated;

        return DjiRenderPoseSource.None;
    }
}

/// <summary>
/// Keeps the render-time budget independent from the older visual/telemetry
/// baseline used by the rotation-only prediction mathematics.
/// </summary>
public static class RotationPropagationTiming
{
    public const long NoTimestamp = 0L;

    public static long StartIfNeeded(long propagationStartTimestampNs, long nowTimestampNs) =>
        propagationStartTimestampNs > NoTimestamp ? propagationStartTimestampNs : nowTimestampNs;

    public static long Reset() => NoTimestamp;

    public static float AgeSeconds(long nowTimestampNs, long propagationStartTimestampNs)
    {
        if (propagationStartTimestampNs <= NoTimestamp || nowTimestampNs < propagationStartTimestampNs)
            return float.PositiveInfinity;

        return (float)((nowTimestampNs - propagationStartTimestampNs) / 1_000_000_000.0);
    }
}
