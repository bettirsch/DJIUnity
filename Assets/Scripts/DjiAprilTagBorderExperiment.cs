using UnityEngine;

public static class DjiAprilTagBorderExperiment
{
    public static bool Enabled
    {
        get
        {
#if DJI_APRILTAG_BORDER_48_EXPERIMENT
            return true;
#else
            return false;
#endif
        }
    }

    public static float Resolve(float productionThresholdPixels, int markerCount,
        bool isReacquisition, bool experimentEnabled) =>
        experimentEnabled && isReacquisition && markerCount == 4
            ? Mathf.Min(productionThresholdPixels, 48f)
            : productionThresholdPixels;
}

public sealed partial class DjiBoardVisionProvider
{
    private bool IsBorderExperimentReacquisition() => RequiresVisualReacquisitionHandover();

#if DJI_APRILTAG_BORDER_48_EXPERIMENT
    private bool _borderDiagnosticReacquisition;
    private float _borderDiagnosticThreshold;
    private long _borderDiagnosticDwellStartNs;

    private void BeginBorderExperimentDiagnostic(NativeBoardResult result)
    {
        _borderDiagnosticReacquisition = IsBorderExperimentReacquisition();
        _borderDiagnosticThreshold = DjiAprilTagBorderExperiment.Resolve(
            highConfidenceMinimumBorderDistancePixels, result.markerCount,
            _borderDiagnosticReacquisition, DjiAprilTagBorderExperiment.Enabled);
        _borderDiagnosticDwellStartNs = _visualReacquisitionStartTimestampNs;
    }

    private void FillBorderExperimentDiagnostic(OrientationDiagnosticRecord record, Pose pose)
    {
        record.hasBorderExperimentDiagnostics = true;
        record.borderExperimentEnabled = DjiAprilTagBorderExperiment.Enabled;
        record.borderReacquisition = _borderDiagnosticReacquisition;
        record.activeBorderThresholdPixels = _borderDiagnosticThreshold;
        record.candidatePosition = pose.position;
        record.borderMetricsAvailable = _orientationMetrics.timestampNs == record.imageTimeNs && _orientationMetrics.markerCount > 0;
        if (record.borderMetricsAvailable && _retentionEvaluationTimestampNs == record.imageTimeNs)
            record.activeBorderThresholdPixels = _retentionEffectiveThresholdPixels;
        record.borderEligible = record.poseValid && record.borderMetricsAvailable &&
            _orientationMetrics.minimumBorderDistancePixels >= record.activeBorderThresholdPixels;
        record.qualityConfirmationCount = _consecutiveHighConfidenceVisualPoses;
        record.qualityConfirmationRequired = highConfidenceReacquisitionConfirmationFrames;
        var start = _hasVisualReacquisitionCandidate
            ? _visualReacquisitionStartTimestampNs : _borderDiagnosticDwellStartNs;
        record.reacquisitionDwellMilliseconds = start > 0L && record.imageTimeNs >= start
            ? (record.imageTimeNs - start) / 1_000_000d : 0d;
        record.requiredReacquisitionDwellMilliseconds = reacquisitionStableTimeSeconds * 1000d;
        record.candidateTranslationJumpMeters = _orientationMetrics.translationJumpMeters;
        record.candidateRotationJumpDegrees = _orientationMetrics.rotationJumpDegrees;
    }
#endif
}
