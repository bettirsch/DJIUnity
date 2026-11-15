using System;
using UnityEngine;

public enum VisualTranslationJumpDecision
{
    Accept,
    Suspect,
    Reject
}

public enum FourMarkerVisualQualityFailureSeverity
{
    Pass,
    Borderline,
    Severe
}

public readonly struct VisualTranslationJumpEvaluation
{
    public readonly VisualTranslationJumpDecision decision;
    public readonly float impliedSpeedMetersPerSecond;
    public readonly float telemetrySpeedMetersPerSecond;
    public readonly float rotationCompensatedResidualMeters;

    public VisualTranslationJumpEvaluation(
        VisualTranslationJumpDecision decision,
        float impliedSpeedMetersPerSecond,
        float telemetrySpeedMetersPerSecond,
        float rotationCompensatedResidualMeters)
    {
        this.decision = decision;
        this.impliedSpeedMetersPerSecond = impliedSpeedMetersPerSecond;
        this.telemetrySpeedMetersPerSecond = telemetrySpeedMetersPerSecond;
        this.rotationCompensatedResidualMeters = rotationCompensatedResidualMeters;
    }
}

/// <summary>
/// Pure render-quality policy. It intentionally keeps PnP acceptance separate
/// from whether a visual pose is safe enough to move rendered content.
/// </summary>
public static class DjiVisualPoseQualityPolicy
{
    public static float NormalizePixelResidual(float residualPixels, float observedMarkerSizePixels)
    {
        return float.IsFinite(residualPixels) &&
               float.IsFinite(observedMarkerSizePixels) &&
               observedMarkerSizePixels > 0f
            ? residualPixels / observedMarkerSizePixels
            : float.PositiveInfinity;
    }

    public static bool IsBorderlineFourMarkerNormalizedCornerResidual(
        float normalizedCornerResidual,
        float maximumNormalizedCornerResidual,
        float borderlineMaximumNormalizedCornerResidual)
    {
        return float.IsFinite(normalizedCornerResidual) &&
               normalizedCornerResidual > maximumNormalizedCornerResidual &&
               normalizedCornerResidual <= borderlineMaximumNormalizedCornerResidual;
    }

    public static bool IsBorderlineFourMarkerBorderProximity(
        float borderDistancePixels,
        float minimumRequiredBorderDistancePixels,
        float severeBorderDistancePixels)
    {
        return float.IsFinite(borderDistancePixels) &&
               borderDistancePixels < minimumRequiredBorderDistancePixels &&
               borderDistancePixels >= severeBorderDistancePixels;
    }

    public static int AdvanceBorderlineFailureCount(
        int currentCount,
        FourMarkerVisualQualityFailureSeverity severity)
    {
        return severity == FourMarkerVisualQualityFailureSeverity.Borderline
            ? currentCount + 1
            : 0;
    }

    public static bool IsBorderlineFailureConfirmed(int consecutiveFailures, int confirmationFrames)
    {
        return consecutiveFailures >= Mathf.Max(1, confirmationFrames);
    }

    public static bool ShouldHoldVisualAuthorityForBorderlineFailure(
        bool hasVisualAuthority,
        int consecutiveFailures,
        int confirmationFrames)
    {
        return hasVisualAuthority &&
               !IsBorderlineFailureConfirmed(consecutiveFailures, confirmationFrames);
    }

    /// <summary>
    /// Four simultaneously visible markers provide enough image geometry to
    /// assess reprojection error relative to the observed marker scale. This
    /// prevents a physically similar residual from being treated differently
    /// solely because the board is nearer to the camera.
    /// </summary>
    public static string EvaluateFourMarkerScaleAware(
        float reprojectionRmsPixels,
        float maxCornerResidualPixels,
        float observedMarkerSizePixels,
        float maximumNormalizedRms,
        float maximumNormalizedCornerResidual,
        float hardMaximumRmsPixels,
        float hardMaximumCornerResidualPixels)
    {
        if (!float.IsFinite(observedMarkerSizePixels) || observedMarkerSizePixels <= 0f)
            return "OBSERVED_MARKER_SIZE_INVALID";
        if (!float.IsFinite(reprojectionRmsPixels) || reprojectionRmsPixels > hardMaximumRmsPixels)
            return "ABSOLUTE_RMS_HARD_CAP";
        if (!float.IsFinite(maxCornerResidualPixels) || maxCornerResidualPixels > hardMaximumCornerResidualPixels)
            return "ABSOLUTE_CORNER_HARD_CAP";

        if (NormalizePixelResidual(reprojectionRmsPixels, observedMarkerSizePixels) > maximumNormalizedRms)
            return "NORMALIZED_RMS";
        if (NormalizePixelResidual(maxCornerResidualPixels, observedMarkerSizePixels) > maximumNormalizedCornerResidual)
            return "NORMALIZED_CORNER_RESIDUAL";

        return string.Empty;
    }

    public static float ReprojectionRmsThreshold(
        int markerCount,
        float defaultMaximumReprojectionRmsPixels,
        float fourMarkerMaximumReprojectionRmsPixels)
    {
        return markerCount == 4
            ? fourMarkerMaximumReprojectionRmsPixels
            : defaultMaximumReprojectionRmsPixels;
    }

    public static float CornerResidualThreshold(
        int markerCount,
        float defaultMaximumCornerResidualPixels,
        float fourMarkerMaximumCornerResidualPixels)
    {
        return markerCount == 4
            ? fourMarkerMaximumCornerResidualPixels
            : defaultMaximumCornerResidualPixels;
    }

    public static string Evaluate(
        int markerCount,
        float reprojectionRmsPixels,
        float maxCornerResidualPixels,
        float minimumBorderDistancePixels,
        float translationJumpMeters,
        float rotationJumpDegrees,
        bool allowSingleMarkerHighConfidence,
        float defaultMaximumReprojectionRmsPixels,
        float fourMarkerMaximumReprojectionRmsPixels,
        float maximumCornerResidualPixels,
        float fourMarkerMaximumCornerResidualPixels,
        float minimumBorderDistancePixelsRequired,
        float maximumTranslationJumpMeters,
        float maximumRotationJumpDegrees,
        bool evaluateTranslationJump = true,
        bool evaluateFourMarkerPixelResiduals = true)
    {
        if (markerCount <= 0)
            return "NO_MARKERS_DETECTED";
        if (!allowSingleMarkerHighConfidence && markerCount < 2)
            return "INSUFFICIENT_MARKERS";

        if (markerCount != 4 || evaluateFourMarkerPixelResiduals)
        {
            var rmsThreshold = ReprojectionRmsThreshold(
                markerCount,
                defaultMaximumReprojectionRmsPixels,
                fourMarkerMaximumReprojectionRmsPixels);
            if (!float.IsFinite(reprojectionRmsPixels) || reprojectionRmsPixels > rmsThreshold)
                return "REPROJECTION_RMS";
            var cornerResidualThreshold = CornerResidualThreshold(
                markerCount,
                maximumCornerResidualPixels,
                fourMarkerMaximumCornerResidualPixels);
            if (!float.IsFinite(maxCornerResidualPixels) || maxCornerResidualPixels > cornerResidualThreshold)
                return "CORNER_RESIDUAL";
        }
        if (!float.IsFinite(minimumBorderDistancePixels) ||
            minimumBorderDistancePixels < minimumBorderDistancePixelsRequired)
        {
            return "BORDER_PROXIMITY";
        }
        if (evaluateTranslationJump && translationJumpMeters > maximumTranslationJumpMeters)
            return "POSE_TRANSLATION_JUMP";
        if (rotationJumpDegrees > maximumRotationJumpDegrees)
            return "POSE_ROTATION_JUMP";

        return string.Empty;
    }

    /// <summary>
    /// Evaluates the four-marker translation safeguard after the independent
    /// image-space gates have passed. Camera-space board translation changes
    /// naturally as the gimbal rotates, so the residual after that measured
    /// rotation is the useful quantity; velocity only provides a diagnostic
    /// motion bound and never contributes to a rendered position.
    /// </summary>
    public static VisualTranslationJumpEvaluation EvaluateMotionAwareTranslationJump(
        float translationJumpMeters,
        float elapsedSeconds,
        bool hasRotationPrediction,
        float rotationCompensatedResidualMeters,
        bool hasTelemetrySpeed,
        float telemetrySpeedMetersPerSecond,
        float maximumNormalJumpMeters,
        float immediateRejectJumpMeters,
        float motionResidualAllowanceMeters,
        float telemetrySpeedAllowanceMultiplier)
    {
        var impliedSpeedMetersPerSecond = elapsedSeconds > 0f
            ? translationJumpMeters / elapsedSeconds
            : float.PositiveInfinity;
        var safeTelemetrySpeed = hasTelemetrySpeed && float.IsFinite(telemetrySpeedMetersPerSecond)
            ? Mathf.Max(0f, telemetrySpeedMetersPerSecond)
            : float.NaN;

        if (!float.IsFinite(translationJumpMeters) || translationJumpMeters >= immediateRejectJumpMeters)
        {
            return new VisualTranslationJumpEvaluation(
                VisualTranslationJumpDecision.Reject,
                impliedSpeedMetersPerSecond,
                safeTelemetrySpeed,
                rotationCompensatedResidualMeters);
        }

        if (translationJumpMeters <= maximumNormalJumpMeters)
        {
            return new VisualTranslationJumpEvaluation(
                VisualTranslationJumpDecision.Accept,
                impliedSpeedMetersPerSecond,
                safeTelemetrySpeed,
                rotationCompensatedResidualMeters);
        }

        if (hasRotationPrediction && float.IsFinite(rotationCompensatedResidualMeters))
        {
            var velocityAllowance = hasTelemetrySpeed && elapsedSeconds > 0f
                ? safeTelemetrySpeed * elapsedSeconds * telemetrySpeedAllowanceMultiplier
                : 0f;
            var plausibleResidualMeters = motionResidualAllowanceMeters + velocityAllowance;
            if (rotationCompensatedResidualMeters <= plausibleResidualMeters)
            {
                return new VisualTranslationJumpEvaluation(
                    VisualTranslationJumpDecision.Accept,
                    impliedSpeedMetersPerSecond,
                    safeTelemetrySpeed,
                    rotationCompensatedResidualMeters);
            }
        }

        return new VisualTranslationJumpEvaluation(
            VisualTranslationJumpDecision.Suspect,
            impliedSpeedMetersPerSecond,
            safeTelemetrySpeed,
            rotationCompensatedResidualMeters);
    }

    public static bool IsTranslationJumpConfirmed(int consecutiveSuspects, int requiredSuspects) =>
        consecutiveSuspects >= Math.Max(1, requiredSuspects);
}
