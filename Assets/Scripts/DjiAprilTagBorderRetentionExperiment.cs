using System;
using System.Diagnostics;
using UnityEngine;

public static class DjiAprilTagBorderRetentionExperiment
{
    public static bool Enabled
    {
        get
        {
#if DJI_APRILTAG_BORDER_RETENTION_EXPERIMENT
            return true;
#else
            return false;
#endif
        }
    }

    public static bool IsEligible(int markerCount, float borderPixels, DjiRenderPoseSource authority,
        bool previousHighConfidence, bool enabled) =>
        enabled && authority == DjiRenderPoseSource.Visual && previousHighConfidence &&
        markerCount == 4 && float.IsFinite(borderPixels) && borderPixels >= 48f;

    public static float Resolve(float existingThresholdPixels, int markerCount, float borderPixels,
        DjiRenderPoseSource authority, bool previousHighConfidence, bool enabled) =>
        IsEligible(markerCount, borderPixels, authority, previousHighConfidence, enabled)
            ? Mathf.Min(existingThresholdPixels, 48f) : existingThresholdPixels;
}

public sealed partial class DjiBoardVisionProvider
{
    // Observability only; quality decisions use the pure selector above.
    private long _retentionEvaluationTimestampNs;
    private float _retentionEffectiveThresholdPixels;
    private bool _retentionEnabledForEvaluation, _retentionEligibleForEvaluation, _retentionPreviousHighConfidence;
    private string _retentionAuthorityForEvaluation;
    private long _retentionLastAppliedVisualTimestampNs;

    private void RecordBorderRetentionEvaluation(VisualPoseMetrics metrics, float threshold, bool enabled)
    {
        _retentionEvaluationTimestampNs = metrics.timestampNs;
        _retentionEffectiveThresholdPixels = threshold;
        _retentionEnabledForEvaluation = enabled;
        _retentionPreviousHighConfidence = CurrentVisualPoseQuality == VisualPoseQuality.HighConfidence;
        _retentionAuthorityForEvaluation = CurrentRenderPoseSource.ToString();
        _retentionEligibleForEvaluation = DjiAprilTagBorderRetentionExperiment.IsEligible(
            metrics.markerCount, metrics.minimumBorderDistancePixels, CurrentRenderPoseSource,
            _retentionPreviousHighConfidence, enabled);
    }

    private void FillBorderRetentionDiagnostic(OrientationDiagnosticRecord record)
    {
        record.hasBorderRetentionDiagnostics = true;
        record.borderRetentionEnabled = DjiAprilTagBorderRetentionExperiment.Enabled;
        record.borderRetentionMetricsAvailable = _retentionEvaluationTimestampNs == record.imageTimeNs &&
            _orientationMetrics.timestampNs == record.imageTimeNs && _orientationMetrics.markerCount > 0;
        if (!record.borderRetentionMetricsAvailable) return;
        record.borderRetentionEnabled = _retentionEnabledForEvaluation;
        record.borderRetentionEligible = _retentionEligibleForEvaluation;
        record.borderRetentionPreviousHighConfidence = _retentionPreviousHighConfidence;
        record.borderRetentionAuthorityAtEvaluation = _retentionAuthorityForEvaluation;
        record.borderRetentionThresholdPixels = _retentionEffectiveThresholdPixels;
        record.borderRetentionLegacy64Eligible = float.IsFinite(_orientationMetrics.minimumBorderDistancePixels) &&
            _orientationMetrics.minimumBorderDistancePixels >= 64f;
        record.borderRetentionCandidatePromoted = _orientationPromoted;
    }

    private void TraceBorderRetentionVisualApplication(Pose appliedPose)
    {
        if (CurrentRenderPoseSource != DjiRenderPoseSource.Visual || !HasHighConfidenceCameraFromBoard ||
            _currentHighConfidenceVisualTimestampNs <= 0 ||
            _currentHighConfidenceVisualTimestampNs == _retentionLastAppliedVisualTimestampNs) return;
        try
        {
            _telemetryValidationLogger?.WriteBorderRetentionVisualApplication(
                DjiBoardVisionBridge.GetMonotonicTimestampNs(), _currentHighConfidenceVisualTimestampNs, appliedPose);
            _retentionLastAppliedVisualTimestampNs = _currentHighConfidenceVisualTimestampNs;
        }
        catch (Exception) { /* Diagnostics never affect pose application. */ }
    }
}

public sealed partial class DjiTelemetryValidationLogger
{
    public void WriteBorderRetentionVisualApplication(long appliedNs, long visualSourceNs, Pose pose)
    {
        if (_visualLatencyWriter == null || !isActiveAndEnabled) return;
        try
        {
            var start = Stopwatch.GetTimestamp();
            _visualLatencyWriter.WriteLine(BuildBorderRetentionApplicationJson(_telemetrySessionId, appliedNs, visualSourceNs, pose));
            if (_visualLatencyWriter is BoundedDiagnosticJsonlWriter buffered)
                buffered.ObserveSerialization(Stopwatch.GetTimestamp() - start);
        }
        catch (Exception)
        {
            if (_visualLatencyWriter is BoundedDiagnosticJsonlWriter buffered) buffered.ReportCaptureError();
        }
    }

    public static string BuildBorderRetentionApplicationJson(string session, long appliedNs, long visualSourceNs, Pose pose)
    {
        var j = MotionEventHeader("VisualBorderRetentionPoseApplied", session, appliedNs);
        j.Property("aprilTagBorderRetentionExperimentEnabled").Boolean(true);
        j.Property("visualSourceTimestampNs").Number(visualSourceNs);
        j.Property("renderApplicationTimestampNs").Number(appliedNs);
        j.Property("renderAuthority").String("Visual");
        j.Property("newVisualPoseApplied").Boolean(true);
        j.Property("poseFrame").String("T_camera_board_Board_to_OpenCV_OpticalCamera");
        WriteTemporalPose(j, "appliedCameraFromBoardPosition", "appliedCameraFromBoardRotation", pose);
        return j.EndObject().ToString();
    }
}
