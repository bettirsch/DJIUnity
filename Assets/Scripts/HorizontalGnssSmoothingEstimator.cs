using System;
using UnityEngine;

/// <summary>
/// Diagnostic-only horizontal GNSS target smoother. It owns no world transform
/// and never changes render authority or the authoritative localization state.
/// </summary>
public sealed class HorizontalGnssSmoothingEstimator
{
    public const float DefaultTauSeconds = 0.5f;
    public const float DefaultMaximumCorrectionSpeedMetersPerSecond = 2.5f;
    // Provisional physical-test tuning: reject short-term GNSS wander until it
    // persists across two causally ordered samples in a consistent direction.
    public const float ProvisionalStationaryNoiseEnvelopeMeters = 0.08f;
    public const int ProvisionalTemporalConsistencySampleCount = 2;
    private const float ConsistentDirectionDotProduct = 0.5f;

    private readonly float _tauSeconds;
    private readonly float _maximumCorrectionSpeedMetersPerSecond;
    private bool _initialized;
    private Vector2 _estimate;
    private Vector2 _latestRawTarget;
    private Vector2 _acceptedTarget;
    private Vector2 _pendingTargetDirection;
    private long _lastEstimatorTimestampNs;
    private long _latestRawTargetTimestampNs;
    private long _acceptedTargetTimestampNs;
    private bool _hasAcceptedTarget;
    private int _temporalConsistencyCount;

    public HorizontalGnssSmoothingEstimator(
        float tauSeconds = DefaultTauSeconds,
        float maximumCorrectionSpeedMetersPerSecond = DefaultMaximumCorrectionSpeedMetersPerSecond)
    {
        _tauSeconds = Mathf.Max(0.001f, tauSeconds);
        _maximumCorrectionSpeedMetersPerSecond = Mathf.Max(0.001f, maximumCorrectionSpeedMetersPerSecond);
    }

    public bool IsInitialized => _initialized;

    public void Reset()
    {
        _initialized = false;
        _estimate = Vector2.zero;
        _latestRawTarget = Vector2.zero;
        _acceptedTarget = Vector2.zero;
        _pendingTargetDirection = Vector2.zero;
        _lastEstimatorTimestampNs = 0L;
        _latestRawTargetTimestampNs = 0L;
        _acceptedTargetTimestampNs = 0L;
        _hasAcceptedTarget = false;
        _temporalConsistencyCount = 0;
    }

    public bool Initialize(long baselineTimestampNs)
    {
        Reset();
        if (baselineTimestampNs <= 0L)
            return false;

        _initialized = true;
        _lastEstimatorTimestampNs = baselineTimestampNs;
        return true;
    }

    public HorizontalGnssSmoothingEstimate Update(
        long targetTimestampNs,
        bool hasFreshGnss,
        Vector2 rawGnssTarget,
        long rawGnssTimestampNs)
    {
        if (!_initialized)
            return HorizontalGnssSmoothingEstimate.Invalid("NOT_INITIALIZED", targetTimestampNs, _tauSeconds, _maximumCorrectionSpeedMetersPerSecond);

        if (targetTimestampNs < _lastEstimatorTimestampNs)
            return HorizontalGnssSmoothingEstimate.Invalid("NON_MONOTONIC_TARGET", targetTimestampNs, _tauSeconds, _maximumCorrectionSpeedMetersPerSecond);

        var deltaTimeSeconds = (targetTimestampNs - _lastEstimatorTimestampNs) / 1_000_000_000d;
        if (hasFreshGnss &&
            rawGnssTimestampNs > 0L &&
            rawGnssTimestampNs <= targetTimestampNs &&
            IsFinite(rawGnssTarget))
        {
            var targetAccepted = false;
            var targetRejectedReason = "NO_NEW_GNSS_TARGET";
            var distanceFromAcceptedTarget = _hasAcceptedTarget
                ? Vector2.Distance(rawGnssTarget, _acceptedTarget)
                : 0f;

            // A repeated causal GNSS sample remains a valid measurement, but it
            // cannot advance the target-consistency state at render frequency.
            if (rawGnssTimestampNs > _latestRawTargetTimestampNs)
            {
                _latestRawTarget = rawGnssTarget;
                _latestRawTargetTimestampNs = rawGnssTimestampNs;
                targetAccepted = TryAcceptNewRawTarget(
                    rawGnssTarget,
                    rawGnssTimestampNs,
                    out targetRejectedReason,
                    out distanceFromAcceptedTarget);
            }

            var alpha = (float)(1d - Math.Exp(-deltaTimeSeconds / _tauSeconds));
            var desiredCorrection = (_acceptedTarget - _estimate) * alpha;
            var maximumStep = _maximumCorrectionSpeedMetersPerSecond * (float)deltaTimeSeconds;
            var appliedCorrection = Vector2.ClampMagnitude(desiredCorrection, maximumStep);
            _estimate += appliedCorrection;
            _lastEstimatorTimestampNs = targetTimestampNs;
            var correctionSpeed = deltaTimeSeconds > 0d
                ? appliedCorrection.magnitude / (float)deltaTimeSeconds
                : 0f;

            return new HorizontalGnssSmoothingEstimate(
                true,
                HorizontalGnssSmoothingState.FollowingFreshGnss,
                targetAccepted ? string.Empty : targetRejectedReason,
                targetTimestampNs,
                _latestRawTargetTimestampNs,
                _latestRawTarget,
                _estimate,
                desiredCorrection,
                appliedCorrection,
                correctionSpeed,
                _tauSeconds,
                _maximumCorrectionSpeedMetersPerSecond,
                _acceptedTarget,
                _acceptedTargetTimestampNs,
                targetAccepted,
                targetRejectedReason,
                distanceFromAcceptedTarget,
                _temporalConsistencyCount);
        }

        // Advance time while stale so a returning GNSS target cannot turn the
        // stale interval into a large catch-up step. No extrapolation occurs.
        _lastEstimatorTimestampNs = targetTimestampNs;
        return new HorizontalGnssSmoothingEstimate(
            false,
            HorizontalGnssSmoothingState.DegradedStaleGnss,
            "GNSS_TARGET_STALE",
            targetTimestampNs,
            _latestRawTargetTimestampNs,
            _latestRawTarget,
            _estimate,
            Vector2.zero,
            Vector2.zero,
            0f,
            _tauSeconds,
            _maximumCorrectionSpeedMetersPerSecond,
            _acceptedTarget,
            _acceptedTargetTimestampNs,
            false,
            "GNSS_TARGET_STALE",
            0f,
            _temporalConsistencyCount);
    }

    /// <summary>
    /// Retains the existing estimate while GNSS source quality is untrusted.
    /// Time advances so recovery never creates a catch-up correction.
    /// </summary>
    public HorizontalGnssSmoothingEstimate FreezeForGnssQuality(
        long targetTimestampNs,
        string admissionReason)
    {
        if (!_initialized)
            return HorizontalGnssSmoothingEstimate.Invalid("NOT_INITIALIZED", targetTimestampNs, _tauSeconds, _maximumCorrectionSpeedMetersPerSecond);

        if (targetTimestampNs < _lastEstimatorTimestampNs)
            return HorizontalGnssSmoothingEstimate.Invalid("NON_MONOTONIC_TARGET", targetTimestampNs, _tauSeconds, _maximumCorrectionSpeedMetersPerSecond);

        _lastEstimatorTimestampNs = targetTimestampNs;
        return new HorizontalGnssSmoothingEstimate(
            false,
            HorizontalGnssSmoothingState.DegradedGnssQuality,
            admissionReason,
            targetTimestampNs,
            _latestRawTargetTimestampNs,
            _latestRawTarget,
            _estimate,
            Vector2.zero,
            Vector2.zero,
            0f,
            _tauSeconds,
            _maximumCorrectionSpeedMetersPerSecond,
            _acceptedTarget,
            _acceptedTargetTimestampNs,
            false,
            admissionReason,
            0f,
            _temporalConsistencyCount);
    }

    private bool TryAcceptNewRawTarget(
        Vector2 rawTarget,
        long rawTimestampNs,
        out string rejectionReason,
        out float distanceFromAcceptedTarget)
    {
        if (!_hasAcceptedTarget)
        {
            _acceptedTarget = rawTarget;
            _acceptedTargetTimestampNs = rawTimestampNs;
            _hasAcceptedTarget = true;
            _temporalConsistencyCount = 0;
            _pendingTargetDirection = Vector2.zero;
            rejectionReason = string.Empty;
            distanceFromAcceptedTarget = 0f;
            return true;
        }

        var delta = rawTarget - _acceptedTarget;
        distanceFromAcceptedTarget = delta.magnitude;
        if (distanceFromAcceptedTarget < ProvisionalStationaryNoiseEnvelopeMeters)
        {
            _temporalConsistencyCount = 0;
            _pendingTargetDirection = Vector2.zero;
            rejectionReason = "WITHIN_STATIONARY_NOISE_ENVELOPE";
            return false;
        }

        var direction = delta / distanceFromAcceptedTarget;
        var directionIsConsistent = _temporalConsistencyCount > 0 &&
                                  Vector2.Dot(_pendingTargetDirection, direction) >= ConsistentDirectionDotProduct;
        _temporalConsistencyCount = directionIsConsistent ? _temporalConsistencyCount + 1 : 1;
        _pendingTargetDirection = direction;
        if (_temporalConsistencyCount < ProvisionalTemporalConsistencySampleCount)
        {
            rejectionReason = "PENDING_TEMPORAL_CONSISTENCY";
            return false;
        }

        _acceptedTarget = rawTarget;
        _acceptedTargetTimestampNs = rawTimestampNs;
        _temporalConsistencyCount = 0;
        _pendingTargetDirection = Vector2.zero;
        rejectionReason = string.Empty;
        return true;
    }

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.x) && float.IsFinite(value.y);
}

public enum HorizontalGnssSmoothingState
{
    Uninitialized,
    FollowingFreshGnss,
    DegradedStaleGnss,
    DegradedGnssQuality
}

public readonly struct HorizontalGnssSmoothingEstimate
{
    public readonly bool valid;
    public readonly HorizontalGnssSmoothingState state;
    public readonly string failureReason;
    public readonly long targetTimestampNs;
    public readonly long rawGnssTargetTimestampNs;
    public readonly Vector2 rawGnssTarget;
    public readonly Vector2 smoothedNavigationDelta;
    public readonly Vector2 desiredCorrection;
    public readonly Vector2 appliedCorrection;
    public readonly float correctionSpeedMetersPerSecond;
    public readonly float tauSeconds;
    public readonly float maximumCorrectionSpeedMetersPerSecond;
    public readonly Vector2 acceptedGnssTarget;
    public readonly long acceptedGnssTargetTimestampNs;
    public readonly bool targetAccepted;
    public readonly string targetRejectedReason;
    public readonly float distanceFromAcceptedTargetMeters;
    public readonly int temporalConsistencyCount;

    public bool hasEstimate => state != HorizontalGnssSmoothingState.Uninitialized;

    public HorizontalGnssSmoothingEstimate(
        bool valid,
        HorizontalGnssSmoothingState state,
        string failureReason,
        long targetTimestampNs,
        long rawGnssTargetTimestampNs,
        Vector2 rawGnssTarget,
        Vector2 smoothedNavigationDelta,
        Vector2 desiredCorrection,
        Vector2 appliedCorrection,
        float correctionSpeedMetersPerSecond,
        float tauSeconds,
        float maximumCorrectionSpeedMetersPerSecond,
        Vector2 acceptedGnssTarget = default,
        long acceptedGnssTargetTimestampNs = 0L,
        bool targetAccepted = false,
        string targetRejectedReason = "",
        float distanceFromAcceptedTargetMeters = 0f,
        int temporalConsistencyCount = 0)
    {
        this.valid = valid;
        this.state = state;
        this.failureReason = failureReason ?? string.Empty;
        this.targetTimestampNs = targetTimestampNs;
        this.rawGnssTargetTimestampNs = rawGnssTargetTimestampNs;
        this.rawGnssTarget = rawGnssTarget;
        this.smoothedNavigationDelta = smoothedNavigationDelta;
        this.desiredCorrection = desiredCorrection;
        this.appliedCorrection = appliedCorrection;
        this.correctionSpeedMetersPerSecond = correctionSpeedMetersPerSecond;
        this.tauSeconds = tauSeconds;
        this.maximumCorrectionSpeedMetersPerSecond = maximumCorrectionSpeedMetersPerSecond;
        this.acceptedGnssTarget = acceptedGnssTarget;
        this.acceptedGnssTargetTimestampNs = acceptedGnssTargetTimestampNs;
        this.targetAccepted = targetAccepted;
        this.targetRejectedReason = targetRejectedReason ?? string.Empty;
        this.distanceFromAcceptedTargetMeters = distanceFromAcceptedTargetMeters;
        this.temporalConsistencyCount = temporalConsistencyCount;
    }

    public static HorizontalGnssSmoothingEstimate Invalid(
        string reason,
        long targetTimestampNs,
        float tauSeconds,
        float maximumCorrectionSpeedMetersPerSecond) =>
        new(
            false,
            HorizontalGnssSmoothingState.Uninitialized,
            reason,
            targetTimestampNs,
            0L,
            Vector2.zero,
            Vector2.zero,
            Vector2.zero,
            Vector2.zero,
            0f,
            tauSeconds,
            maximumCorrectionSpeedMetersPerSecond);
}
