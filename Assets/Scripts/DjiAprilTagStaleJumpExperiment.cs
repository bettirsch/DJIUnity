using System;
using UnityEngine;

/// <summary>Experimental evidence gate, not a motion estimate or a distance allowance.</summary>
public sealed class StaleVisualJumpCluster
{
    public bool Active { get; private set; }
    public int Count { get; private set; }
    private Pose _anchor;
    private long _lastTimestampNs;

    public void Reset() { Active = false; Count = 0; _lastTimestampNs = 0; }

    public bool Observe(Pose pose, long timestampNs, bool canStart, bool fourTagReacquisition,
        bool independentQualityPassed, VisualTranslationJumpDecision legacyDecision,
        float positionToleranceM, float rotationToleranceDeg, float maximumSampleGapSeconds,
        out bool started, out string reason)
    {
        started = false;
        reason = string.Empty;
        if (!Active && !canStart) return false;
        if (!fourTagReacquisition || !independentQualityPassed || timestampNs <= 0 ||
            !float.IsFinite(pose.position.x) || !float.IsFinite(pose.position.y) ||
            !float.IsFinite(pose.position.z) || !float.IsFinite(pose.rotation.x) ||
            !float.IsFinite(pose.rotation.y) || !float.IsFinite(pose.rotation.z) || !float.IsFinite(pose.rotation.w) ||
            Quaternion.Dot(pose.rotation, pose.rotation) < 0.5f)
        {
            Reset(); reason = "STALE_CLUSTER_QUALITY_REJECTED"; return false;
        }
        if (!Active)
        {
            Active = true; Count = 1; _anchor = pose; _lastTimestampNs = timestampNs;
            started = true; reason = "STALE_BASELINE_REQUIRES_CONFIRMATION"; return true;
        }
        if (timestampNs <= _lastTimestampNs ||
            (timestampNs - _lastTimestampNs) / 1e9 > maximumSampleGapSeconds)
        {
            Reset(); reason = "STALE_CLUSTER_TIMESTAMP_DISCONTINUITY"; return false;
        }
        // Anchor-relative bounds prevent many small, consistent-looking steps
        // from walking the candidate away during confirmation/dwell.
        if (legacyDecision != VisualTranslationJumpDecision.Accept ||
            Vector3.Distance(_anchor.position, pose.position) > positionToleranceM ||
            Quaternion.Angle(_anchor.rotation, pose.rotation) > rotationToleranceDeg)
        {
            Reset(); reason = "STALE_CLUSTER_INCONSISTENT"; return false;
        }
        _lastTimestampNs = timestampNs; Count++;
        reason = "STALE_CLUSTER_CONSISTENT_REQUIRES_EXISTING_DWELL";
        return true;
    }
}

public static class DjiAprilTagStaleJumpExperiment
{
    public static bool Enabled
    {
        get
        {
#if DJI_APRILTAG_STALE_JUMP_EXPERIMENT
            return true;
#else
            return false;
#endif
        }
    }
}

public sealed partial class DjiBoardVisionProvider
{
    private readonly StaleVisualJumpCluster _staleJumpCluster = new();
    private bool _staleJumpTraceAvailable, _staleJumpTraceAllowed;
    private int _staleJumpTraceCount;
    private string _staleJumpTraceReason;
    private VisualTranslationJumpDecision _staleJumpLegacyDecision;
    private double _staleJumpAcceptedAgeMs;

    private VisualPoseQualityEvaluation EvaluateVisualPoseQualityForExperiments(
        VisualPoseMetrics metrics, bool borderExperimentEnabled, bool staleJumpExperimentEnabled)
    {
        return EvaluateVisualPoseQualityForAllExperiments(metrics, borderExperimentEnabled, staleJumpExperimentEnabled,
            DjiAprilTagBorderRetentionExperiment.Enabled);
    }

    private VisualPoseQualityEvaluation EvaluateVisualPoseQualityForAllExperiments(
        VisualPoseMetrics metrics, bool borderExperimentEnabled, bool staleJumpExperimentEnabled, bool retentionExperimentEnabled)
    {
        var legacy = EvaluateLegacyVisualPoseQualityForBorderProfile(metrics, borderExperimentEnabled, retentionExperimentEnabled);
        _staleJumpTraceAvailable = staleJumpExperimentEnabled;
        if (!staleJumpExperimentEnabled) return legacy;

        _staleJumpLegacyDecision = DjiVisualPoseQualityPolicy.EvaluateMotionAwareTranslationJump(
            metrics.translationJumpMeters, metrics.visualPoseDeltaSeconds, metrics.hasTelemetryRotationPrediction,
            metrics.rotationCompensatedTranslationResidualMeters, metrics.hasTelemetrySpeed,
            metrics.telemetrySpeedMetersPerSecond, highConfidenceMaximumFrameTranslationJumpMeters,
            motionAwareImmediateTranslationJumpRejectMeters, motionAwareTranslationResidualAllowanceMeters,
            motionAwareTelemetrySpeedAllowanceMultiplier).decision;
        _staleJumpAcceptedAgeMs = _currentHighConfidenceVisualTimestampNs > 0 &&
            metrics.timestampNs >= _currentHighConfidenceVisualTimestampNs
                ? (metrics.timestampNs - _currentHighConfidenceVisualTimestampNs) / 1e6 : double.NaN;
        var context = RequiresVisualReacquisitionHandover() &&
                      CurrentRenderPoseSource != DjiRenderPoseSource.Visual && metrics.markerCount == 4;
        var independentPassed = string.IsNullOrEmpty(legacy.reason) ||
                                legacy.reason == "POSE_TRANSLATION_JUMP_SEVERE";
        // Reuse the existing short propagation window only to classify an old
        // visual comparison. Elapsed time is never evidence of aircraft motion.
        var canStart = context && independentPassed && float.IsFinite(metrics.translationJumpMeters) &&
                       metrics.visualPoseDeltaSeconds > maxRotationOnlyPropagationSeconds &&
                       _staleJumpAcceptedAgeMs > maxRotationOnlyPropagationSeconds * 1000d &&
                       legacy.reason == "POSE_TRANSLATION_JUMP_SEVERE";
        var wasActive = _staleJumpCluster.Active;
        _staleJumpTraceAllowed = _staleJumpCluster.Observe(metrics.cameraFromBoard, metrics.timestampNs,
            canStart, context, independentPassed, _staleJumpLegacyDecision,
            highConfidenceMaximumFrameTranslationJumpMeters, highConfidenceMaximumFrameRotationJumpDegrees,
            reacquisitionStableTimeSeconds, out var started, out _staleJumpTraceReason);
        _staleJumpTraceCount = _staleJumpCluster.Count;
        if (_staleJumpTraceAllowed)
        {
            if (started)
            {
                CancelVisualReacquisition("STALE_BASELINE_NEW_CONFIRMATION_CLUSTER");
                _consecutiveHighConfidenceVisualPoses = 0;
                SetVisualPoseQuality(VisualPoseQuality.Degraded, metrics, _staleJumpTraceReason);
            }
            // Permission to enter existing confirmation, NOT pose promotion.
            _latestTranslationJumpEvaluation = new VisualTranslationJumpEvaluation(
                VisualTranslationJumpDecision.Accept, _latestTranslationJumpEvaluation.impliedSpeedMetersPerSecond,
                _latestTranslationJumpEvaluation.telemetrySpeedMetersPerSecond,
                _latestTranslationJumpEvaluation.rotationCompensatedResidualMeters);
            return VisualPoseQualityEvaluation.Pass;
        }
        if (wasActive && string.IsNullOrEmpty(legacy.reason))
            return new VisualPoseQualityEvaluation(_staleJumpTraceReason, FourMarkerVisualQualityFailureSeverity.Severe);
        return legacy;
    }

    private void InterruptStaleJumpCluster(string reason)
    {
        if (!_staleJumpCluster.Active) return;
        _staleJumpCluster.Reset();
        _consecutiveHighConfidenceVisualPoses = 0;
        CurrentVisualPoseQuality = VisualPoseQuality.Degraded;
        CancelVisualReacquisition(reason);
    }

    private void FillStaleJumpDiagnostic(OrientationDiagnosticRecord record)
    {
        record.hasStaleJumpDiagnostics = true;
        record.staleJumpExperimentEnabled = true;
        record.staleJumpMetricsAvailable = _staleJumpTraceAvailable &&
            _orientationMetrics.timestampNs == record.imageTimeNs && _orientationMetrics.markerCount > 0;
        record.previousValidVisualAgeMs = _orientationMetrics.visualPoseDeltaSeconds * 1000d;
        record.previousAcceptedVisualAgeMs = _staleJumpAcceptedAgeMs;
        record.staleJumpTranslationM = _orientationMetrics.translationJumpMeters;
        record.legacyAbsoluteJumpRejected = !float.IsFinite(_orientationMetrics.translationJumpMeters) ||
            _orientationMetrics.translationJumpMeters >= motionAwareImmediateTranslationJumpRejectMeters;
        record.legacyTranslationJumpDecision = _staleJumpLegacyDecision.ToString();
        record.experimentalTranslationJumpDecision = _staleJumpTraceAllowed ? "ALLOW_EXISTING_CONFIRMATION" : "LEGACY_OR_REJECT";
        record.staleJumpConfirmationCount = _staleJumpTraceCount;
        record.staleJumpReason = _staleJumpTraceReason;
        record.qualityConfirmationCount = _consecutiveHighConfidenceVisualPoses;
        record.qualityConfirmationRequired = highConfidenceReacquisitionConfirmationFrames;
    }

#if DJI_APRILTAG_STALE_JUMP_EXPERIMENT
    private bool _staleHasAppliedPose;
    private Pose _stalePreviousAppliedPose;
    private long _stalePreviousAppliedTimestampNs;
    private DjiRenderPoseSource _stalePreviousAppliedSource;

    private void TraceStaleJumpAppliedPose(Pose pose)
    {
        try
        {
            var now = DjiBoardVisionBridge.GetMonotonicTimestampNs();
            if (_staleHasAppliedPose && _stalePreviousAppliedSource != DjiRenderPoseSource.Visual &&
                CurrentRenderPoseSource == DjiRenderPoseSource.Visual)
            {
                var record = OrientationRecord("VisualReacquisitionAppliedCorrection");
                record.hasStaleJumpDiagnostics = true; record.staleJumpExperimentEnabled = true;
                record.applicationTimeNs = now;
                record.appliedCorrectionAvailable = true;
                record.previousAppliedTimestampNs = _stalePreviousAppliedTimestampNs;
                record.previousAppliedSource = _stalePreviousAppliedSource.ToString();
                record.previousAppliedCameraInBoard = ReferenceFrameTransforms.Invert(_stalePreviousAppliedPose).position;
                record.appliedCameraInBoard = ReferenceFrameTransforms.Invert(pose).position;
                record.reason = "ACTUAL_RENDER_APPLICATION_DELTA_NOT_PHYSICAL_MOTION_GROUND_TRUTH";
                _telemetryValidationLogger?.WriteOrientationDiagnostic(record);
            }
            _stalePreviousAppliedPose = pose;
            _stalePreviousAppliedSource = CurrentRenderPoseSource;
            _stalePreviousAppliedTimestampNs = now;
            _staleHasAppliedPose = true;
        }
        catch (Exception) { /* Logging never changes rendering. */ }
    }
#endif
}
