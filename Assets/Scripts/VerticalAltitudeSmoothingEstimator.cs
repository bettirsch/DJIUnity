using System;
using UnityEngine;

public enum VerticalAltitudeSmoothingMode
{
    Exponential,
    RawTarget
}

/// <summary>
/// Follows a causally selected AircraftLocation3D altitude displacement in
/// navigation Down. The coordinator converts positive-up source altitude.
/// </summary>
public sealed class VerticalAltitudeSmoothingEstimator
{
    // Historical exponential comparison setting. RawTarget uses zero tau.
    public const float DefaultTauSeconds = 0.3f;
    public const float DefaultMaximumAgeMilliseconds = 300f;

    public const VerticalAltitudeSmoothingMode DefaultMode = VerticalAltitudeSmoothingMode.RawTarget;

    private readonly float _tauSeconds;
    private bool _initialized;
    private float _estimateNavigationDown;
    private float _latestTargetNavigationDown;
    private long _lastEstimatorTimestampNs;
    private long _latestSourceTimestampNs;

    public VerticalAltitudeSmoothingEstimator()
        : this(DefaultMode, DefaultTauSeconds)
    {
    }

    public VerticalAltitudeSmoothingEstimator(float tauSeconds)
        : this(VerticalAltitudeSmoothingMode.Exponential, tauSeconds)
    {
    }

    public VerticalAltitudeSmoothingEstimator(
        VerticalAltitudeSmoothingMode mode,
        float tauSeconds = DefaultTauSeconds)
    {
        if (mode != VerticalAltitudeSmoothingMode.Exponential && mode != VerticalAltitudeSmoothingMode.RawTarget)
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (!float.IsFinite(tauSeconds) || tauSeconds < 0f)
            throw new ArgumentOutOfRangeException(nameof(tauSeconds));

        // Zero is an explicit safe raw-target mode, never an exponential divisor.
        _tauSeconds = mode == VerticalAltitudeSmoothingMode.RawTarget || tauSeconds == 0f
            ? 0f
            : Math.Max(0.001f, tauSeconds);
    }

    public VerticalAltitudeSmoothingMode Mode => _tauSeconds == 0f
        ? VerticalAltitudeSmoothingMode.RawTarget
        : VerticalAltitudeSmoothingMode.Exponential;

    public bool IsInitialized => _initialized;

    public void Reset()
    {
        _initialized = false;
        _estimateNavigationDown = 0f;
        _latestTargetNavigationDown = 0f;
        _lastEstimatorTimestampNs = 0L;
        _latestSourceTimestampNs = 0L;
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

    public VerticalAltitudeSmoothingEstimate Update(
        long targetTimestampNs,
        bool hasFreshAltitude,
        float rawTargetNavigationDown,
        long altitudeSourceTimestampNs)
    {
        if (!_initialized)
            return VerticalAltitudeSmoothingEstimate.BaselineUnavailable(targetTimestampNs, _tauSeconds);

        if (targetTimestampNs < _lastEstimatorTimestampNs)
            return VerticalAltitudeSmoothingEstimate.Invalid("NON_MONOTONIC_TARGET", targetTimestampNs, _tauSeconds);

        var deltaTimeSeconds = (targetTimestampNs - _lastEstimatorTimestampNs) / 1_000_000_000d;
        if (!hasFreshAltitude)
        {
            // Advance the estimator clock without extrapolating. Fresh recovery
            // therefore converges normally instead of creating a catch-up step.
            _lastEstimatorTimestampNs = targetTimestampNs;
            return new VerticalAltitudeSmoothingEstimate(
                VerticalAltitudeLocalizationState.DegradedStale,
                "ALTITUDE_TARGET_STALE",
                targetTimestampNs,
                _latestSourceTimestampNs,
                _latestTargetNavigationDown,
                _estimateNavigationDown,
                0f,
                0f,
                _tauSeconds,
                sourceAccepted: false);
        }

        if (altitudeSourceTimestampNs <= 0L || altitudeSourceTimestampNs > targetTimestampNs || !float.IsFinite(rawTargetNavigationDown))
            return VerticalAltitudeSmoothingEstimate.Invalid("INVALID_ALTITUDE_TARGET", targetTimestampNs, _tauSeconds);

        var sourceAccepted = false;
        if (altitudeSourceTimestampNs > _latestSourceTimestampNs)
        {
            _latestSourceTimestampNs = altitudeSourceTimestampNs;
            _latestTargetNavigationDown = rawTargetNavigationDown;
            sourceAccepted = true;
        }

        var alpha = _tauSeconds == 0f
            ? 1f
            : (float)(1d - Math.Exp(-deltaTimeSeconds / _tauSeconds));
        var desiredCorrection = (_latestTargetNavigationDown - _estimateNavigationDown) * alpha;
        _estimateNavigationDown = _tauSeconds == 0f
            ? _latestTargetNavigationDown
            : _estimateNavigationDown + desiredCorrection;
        _lastEstimatorTimestampNs = targetTimestampNs;
        return new VerticalAltitudeSmoothingEstimate(
            VerticalAltitudeLocalizationState.Active,
            string.Empty,
            targetTimestampNs,
            _latestSourceTimestampNs,
            _latestTargetNavigationDown,
            _estimateNavigationDown,
            desiredCorrection,
            alpha,
            _tauSeconds,
            sourceAccepted);
    }
}

public enum VerticalAltitudeLocalizationState
{
    BaselineUnavailable,
    Active,
    DegradedStale
}

public readonly struct VerticalAltitudeSmoothingEstimate
{
    public readonly VerticalAltitudeLocalizationState state;
    public readonly string failureReason;
    public readonly long targetTimestampNs;
    public readonly long altitudeSourceTimestampNs;
    public readonly float rawTargetNavigationDown;
    public readonly float smoothedNavigationDown;
    public readonly float desiredCorrection;
    public readonly float alpha;
    public readonly float tauSeconds;
    public readonly bool sourceAccepted;

    public bool hasEstimate => state is VerticalAltitudeLocalizationState.Active or VerticalAltitudeLocalizationState.DegradedStale;

    public VerticalAltitudeSmoothingEstimate(
        VerticalAltitudeLocalizationState state,
        string failureReason,
        long targetTimestampNs,
        long altitudeSourceTimestampNs,
        float rawTargetNavigationDown,
        float smoothedNavigationDown,
        float desiredCorrection,
        float alpha,
        float tauSeconds,
        bool sourceAccepted)
    {
        this.state = state;
        this.failureReason = failureReason ?? string.Empty;
        this.targetTimestampNs = targetTimestampNs;
        this.altitudeSourceTimestampNs = altitudeSourceTimestampNs;
        this.rawTargetNavigationDown = rawTargetNavigationDown;
        this.smoothedNavigationDown = smoothedNavigationDown;
        this.desiredCorrection = desiredCorrection;
        this.alpha = alpha;
        this.tauSeconds = tauSeconds;
        this.sourceAccepted = sourceAccepted;
    }

    public static VerticalAltitudeSmoothingEstimate BaselineUnavailable(long targetTimestampNs, float tauSeconds) =>
        new(VerticalAltitudeLocalizationState.BaselineUnavailable, "BASELINE_UNAVAILABLE", targetTimestampNs, 0L, 0f, 0f, 0f, 0f, tauSeconds, false);

    public static VerticalAltitudeSmoothingEstimate Invalid(string reason, long targetTimestampNs, float tauSeconds) =>
        new(VerticalAltitudeLocalizationState.BaselineUnavailable, reason, targetTimestampNs, 0L, 0f, 0f, 0f, 0f, tauSeconds, false);
}

/// <summary>
/// Coordinator-owned production vertical propagation state. It contains only
/// the Location3D altitude contribution; horizontal and rotation remain owned
/// by their existing paths.
/// </summary>
public readonly struct VerticalAltitudeProductionSnapshot
{
    public readonly bool baselineAvailable;
    public readonly bool sourceFresh;
    public readonly double baselineAltitude;
    public readonly double currentAltitude;
    public readonly long baselineAltitudeTimestampNs;
    public readonly long altitudeSourceTimestampNs;
    public readonly double altitudeAgeMilliseconds;
    public readonly float rawDeltaAltitudeUp;
    public readonly float rawDeltaNavigationDown;
    public readonly float smoothedDeltaNavigationDown;
    public readonly Vector3 worldVerticalContribution;
    public readonly VerticalAltitudeSmoothingEstimate estimate;

    public bool hasContribution => estimate.hasEstimate;

    public VerticalAltitudeProductionSnapshot(
        bool baselineAvailable,
        bool sourceFresh,
        double baselineAltitude,
        double currentAltitude,
        long baselineAltitudeTimestampNs,
        long altitudeSourceTimestampNs,
        double altitudeAgeMilliseconds,
        float rawDeltaAltitudeUp,
        float rawDeltaNavigationDown,
        float smoothedDeltaNavigationDown,
        Vector3 worldVerticalContribution,
        VerticalAltitudeSmoothingEstimate estimate)
    {
        this.baselineAvailable = baselineAvailable;
        this.sourceFresh = sourceFresh;
        this.baselineAltitude = baselineAltitude;
        this.currentAltitude = currentAltitude;
        this.baselineAltitudeTimestampNs = baselineAltitudeTimestampNs;
        this.altitudeSourceTimestampNs = altitudeSourceTimestampNs;
        this.altitudeAgeMilliseconds = altitudeAgeMilliseconds;
        this.rawDeltaAltitudeUp = rawDeltaAltitudeUp;
        this.rawDeltaNavigationDown = rawDeltaNavigationDown;
        this.smoothedDeltaNavigationDown = smoothedDeltaNavigationDown;
        this.worldVerticalContribution = worldVerticalContribution;
        this.estimate = estimate;
    }

    public static VerticalAltitudeProductionSnapshot BaselineUnavailable(long targetTimestampNs, string reason) =>
        new(
            baselineAvailable: false,
            sourceFresh: false,
            baselineAltitude: double.NaN,
            currentAltitude: double.NaN,
            baselineAltitudeTimestampNs: 0L,
            altitudeSourceTimestampNs: 0L,
            altitudeAgeMilliseconds: double.PositiveInfinity,
            rawDeltaAltitudeUp: 0f,
            rawDeltaNavigationDown: 0f,
            smoothedDeltaNavigationDown: 0f,
            worldVerticalContribution: Vector3.zero,
            estimate: VerticalAltitudeSmoothingEstimate.Invalid(reason, targetTimestampNs, VerticalAltitudeSmoothingEstimator.DefaultTauSeconds));
}
