using UnityEngine;

/// <summary>
/// Diagnostic-only lifecycle for one authoritative DJI visual outage. The
/// stored poses are T_camera_board. Physical camera displacement is derived
/// directly in the fixed Board frame (+X print-right, +Y print-up, +Z outward),
/// so an outage event remains useful without a telemetry-history lookup.
/// </summary>
public sealed class DjiOutageVisualGroundTruthTracker
{
    public readonly struct TrustedVisualPose
    {
        public readonly Pose cameraFromBoard;
        public readonly long timestampNs;
        public TrustedVisualPose(Pose cameraFromBoard, long timestampNs)
        {
            this.cameraFromBoard = cameraFromBoard;
            this.timestampNs = timestampNs;
        }

        public bool IsValid => timestampNs > 0L;
    }

    public readonly struct OutageContext
    {
        public readonly int outageId;
        public readonly int localizationSessionId;
        public readonly TrustedVisualPose preVisualPose;
        public readonly long outageStartTimestampNs;
        public readonly int horizontalModeSegmentId;
        public readonly HorizontalTranslationRenderMode horizontalRenderMode;
        public readonly bool modeChangedDuringOutage;

        public OutageContext(
            int outageId,
            int localizationSessionId,
            TrustedVisualPose preVisualPose,
            long outageStartTimestampNs,
            int horizontalModeSegmentId,
            HorizontalTranslationRenderMode horizontalRenderMode,
            bool modeChangedDuringOutage)
        {
            this.outageId = outageId;
            this.localizationSessionId = localizationSessionId;
            this.preVisualPose = preVisualPose;
            this.outageStartTimestampNs = outageStartTimestampNs;
            this.horizontalModeSegmentId = horizontalModeSegmentId;
            this.horizontalRenderMode = horizontalRenderMode;
            this.modeChangedDuringOutage = modeChangedDuringOutage;
        }

        public bool IsValid => outageStartTimestampNs > 0L && preVisualPose.IsValid;
    }

    private int _nextOutageId;
    private bool _hasActiveOutage;
    private OutageContext _activeOutage;

    public bool HasActiveOutage => _hasActiveOutage;

    public bool TryBegin(
        int localizationSessionId,
        TrustedVisualPose preVisualPose,
        long outageStartTimestampNs,
        int horizontalModeSegmentId,
        HorizontalTranslationRenderMode horizontalRenderMode,
        out OutageContext outage)
    {
        outage = default;
        if (_hasActiveOutage || !preVisualPose.IsValid || outageStartTimestampNs <= 0L)
            return false;

        _activeOutage = new OutageContext(
            _nextOutageId++,
            localizationSessionId,
            preVisualPose,
            outageStartTimestampNs,
            horizontalModeSegmentId,
            horizontalRenderMode,
            modeChangedDuringOutage: false);
        _hasActiveOutage = true;
        outage = _activeOutage;
        return true;
    }

    public void MarkModeChangedDuringOutage()
    {
        if (!_hasActiveOutage)
            return;

        _activeOutage = new OutageContext(
            _activeOutage.outageId,
            _activeOutage.localizationSessionId,
            _activeOutage.preVisualPose,
            _activeOutage.outageStartTimestampNs,
            _activeOutage.horizontalModeSegmentId,
            _activeOutage.horizontalRenderMode,
            modeChangedDuringOutage: true);
    }

    public bool TryComplete(out OutageContext outage)
    {
        outage = _activeOutage;
        if (!_hasActiveOutage)
            return false;

        _hasActiveOutage = false;
        _activeOutage = default;
        return true;
    }

    public void Reset()
    {
        _nextOutageId = 0;
        _hasActiveOutage = false;
        _activeOutage = default;
    }

    public static Vector3 CameraPositionInBoard(TrustedVisualPose visualPose) =>
        ReferenceFrameTransforms.Invert(visualPose.cameraFromBoard).position;

    public static Vector3 CameraDisplacementInBoard(
        TrustedVisualPose preVisualPose,
        TrustedVisualPose postVisualPose) =>
        CameraPositionInBoard(postVisualPose) - CameraPositionInBoard(preVisualPose);

    public static float HorizontalPredictionErrorInBoard(
        Pose predictedCameraFromBoard,
        Pose visualCameraFromBoard)
    {
        var predictedCameraInBoard = ReferenceFrameTransforms.Invert(predictedCameraFromBoard).position;
        var visualCameraInBoard = ReferenceFrameTransforms.Invert(visualCameraFromBoard).position;
        var errorInBoard = predictedCameraInBoard - visualCameraInBoard;
        return new Vector2(errorInBoard.x, errorInBoard.y).magnitude;
    }
}

public enum DjiOutageReacquisitionMode
{
    PropagatedReacquisition,
    FreshRelocalization
}

/// <summary>One exact pose estimate retained at an authoritative handover.</summary>
public readonly struct DjiOutagePredictionPose
{
    public readonly bool valid;
    public readonly Pose cameraFromBoard;

    public DjiOutagePredictionPose(bool valid, Pose cameraFromBoard)
    {
        this.valid = valid;
        this.cameraFromBoard = cameraFromBoard;
    }
}

/// <summary>
/// Complete, event-level diagnostic record. Positions remain T_camera_board;
/// visualDeltaInBoard uses Board meters (+X print-right, +Y print-up, +Z outward).
/// </summary>
public readonly struct DjiOutageReacquisitionSummary
{
    public readonly DjiOutageVisualGroundTruthTracker.OutageContext outage;
    public readonly DjiOutageReacquisitionMode reacquisitionMode;
    public readonly DjiOutageVisualGroundTruthTracker.TrustedVisualPose postVisualPose;
    public readonly double outageDurationMilliseconds;
    public readonly double propagationDurationMilliseconds;
    public readonly bool visualDisplacementValid;
    public readonly Vector3 visualDeltaInBoard;
    public readonly float visualRotationDifferenceDegrees;
    public readonly DjiOutagePredictionPose rotationOnly;
    public readonly DjiOutagePredictionPose guardedRendered;
    public readonly DjiOutagePredictionPose gnssShadow;
    public readonly DjiOutagePredictionPose hybridShadow;
    public readonly GuardedHorizontalTranslationState guardedStateAtHandover;
    public readonly float appliedCorrectionMeters;
    public readonly float candidateCorrectionMeters;
    public readonly float blendAlpha;

    public DjiOutageReacquisitionSummary(
        DjiOutageVisualGroundTruthTracker.OutageContext outage,
        DjiOutageReacquisitionMode reacquisitionMode,
        DjiOutageVisualGroundTruthTracker.TrustedVisualPose postVisualPose,
        double outageDurationMilliseconds,
        double propagationDurationMilliseconds,
        bool visualDisplacementValid,
        Vector3 visualDeltaInBoard,
        float visualRotationDifferenceDegrees,
        DjiOutagePredictionPose rotationOnly,
        DjiOutagePredictionPose guardedRendered,
        DjiOutagePredictionPose gnssShadow,
        DjiOutagePredictionPose hybridShadow,
        GuardedHorizontalTranslationState guardedStateAtHandover,
        float appliedCorrectionMeters,
        float candidateCorrectionMeters,
        float blendAlpha)
    {
        this.outage = outage;
        this.reacquisitionMode = reacquisitionMode;
        this.postVisualPose = postVisualPose;
        this.outageDurationMilliseconds = outageDurationMilliseconds;
        this.propagationDurationMilliseconds = propagationDurationMilliseconds;
        this.visualDisplacementValid = visualDisplacementValid;
        this.visualDeltaInBoard = visualDeltaInBoard;
        this.visualRotationDifferenceDegrees = visualRotationDifferenceDegrees;
        this.rotationOnly = rotationOnly;
        this.guardedRendered = guardedRendered;
        this.gnssShadow = gnssShadow;
        this.hybridShadow = hybridShadow;
        this.guardedStateAtHandover = guardedStateAtHandover;
        this.appliedCorrectionMeters = appliedCorrectionMeters;
        this.candidateCorrectionMeters = candidateCorrectionMeters;
        this.blendAlpha = blendAlpha;
    }
}
