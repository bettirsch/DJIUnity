using System.Collections.Generic;
using UnityEngine;

public sealed partial class DjiBoardVisionProvider
{
    private readonly struct PendingReacquisitionEvidence
    {
        public readonly NativeBoardResult result;
        public readonly VisualPoseMetrics metrics;
        public readonly long arrivalNs;
        public readonly bool logSnapshot;
        public PendingReacquisitionEvidence(NativeBoardResult result, VisualPoseMetrics metrics, long arrivalNs, bool logSnapshot)
        { this.result = result; this.metrics = metrics; this.arrivalNs = arrivalNs; this.logSnapshot = logSnapshot; }
    }

    private readonly Queue<PendingReacquisitionEvidence> _reacquisitionEvidence = new();
    private long _evidenceLastOfferedNs, _evidenceGeneration;
    private int _evidenceLastSequence;
    private long _validatedEvidenceStartNs, _validatedEvidenceLastNs;
    private int _validatedEvidenceCount;
    private PendingReacquisitionEvidence _lastValidatedEvidence;
    private Quaternion _evidencePrediction;
    private float _evidenceAngle = float.NaN;
    private long _evidenceBeforeNs, _evidenceAfterNs;
    private string _evidenceState = "IDLE";

    private void ResetReacquisitionEvidence()
    {
        _reacquisitionEvidence.Clear();
        _validatedEvidenceStartNs = _validatedEvidenceLastNs = 0;
        _validatedEvidenceCount = 0;
        _lastValidatedEvidence = default;
        _evidenceState = "IDLE";
        // Ordering fences survive a cancelled dwell; a duplicate must not
        // restart an attempt or rejuvenate a frame after timeout.
    }

    private bool PrepareReacquisitionEvidenceOffer(NativeBoardResult result, VisualPoseMetrics metrics, long nowNs)
    {
        if (!DjiValidatedTrackingCandidate.IsFreshPositiveSource(metrics.timestampNs, nowNs, _currentHighConfidenceVisualTimestampNs) ||
            metrics.timestampNs <= _evidenceLastOfferedNs ||
            (result.listenerGeneration > 0 && _evidenceGeneration > result.listenerGeneration) ||
            (result.listenerGeneration > 0 && result.listenerGeneration == _evidenceGeneration && result.frameSequence <= _evidenceLastSequence))
            return false;
        if (result.listenerGeneration > 0 && _evidenceGeneration > 0 && result.listenerGeneration != _evidenceGeneration)
            CancelVisualReacquisition("CAMERA_GENERATION_CHANGED");
        if (_reacquisitionEvidence.Count >= maximumPendingRotationDiagnosticSamples)
        {
            CancelVisualReacquisition("REACQUISITION_EVIDENCE_QUEUE_FULL");
            return false;
        }
        _evidenceLastOfferedNs = metrics.timestampNs;
        if (result.listenerGeneration > 0)
        {
            _evidenceGeneration = result.listenerGeneration;
            _evidenceLastSequence = result.frameSequence;
        }
        return true;
    }

    private void FailReacquisitionEvidence(string reason, long nowNs, PendingReacquisitionEvidence evidence)
    {
        TraceReacquisitionEvidence("INVALID_OR_EXPIRED_EVIDENCE", reason, nowNs, evidence);
        CancelVisualReacquisition(reason);
    }

    private void TryCompleteValidatedReacquisition(float ageSeconds, long nowNs)
    {
        bool longOutage = float.IsFinite(ageSeconds) && !IsWithinShortPropagationWindow(ageSeconds);
        if (CurrentVisualPoseQuality != VisualPoseQuality.HighConfidence || !_hasShadowRotationPropagationBaseline ||
            _telemetryProvider == null || !_telemetryProvider.HasTelemetryOrientation)
        {
            FailReacquisitionEvidence("REACQUISITION_ORIENTATION_INVALID", nowNs, default);
            return;
        }
        while (_reacquisitionEvidence.Count > 0)
        {
            var e = _reacquisitionEvidence.Peek();
            var sourceNs = e.metrics.timestampNs;
            if (nowNs < e.arrivalNs || nowNs - e.arrivalNs > pendingTelemetryBracketTimeoutSeconds * 1e9)
            { FailReacquisitionEvidence("REACQUISITION_BRACKET_TIMEOUT", nowNs, e); return; }
            if (!DjiValidatedTrackingCandidate.IsFreshPositiveSource(sourceNs, nowNs, _currentHighConfidenceVisualTimestampNs) ||
                (e.result.listenerGeneration > 0 && _noMarkersFrameOrder.listenerGeneration > e.result.listenerGeneration))
            { FailReacquisitionEvidence("REACQUISITION_STALE_SOURCE_OR_GENERATION", nowNs, e); return; }

            _evidenceAngle = float.NaN;
            _evidencePrediction = default;
            _evidenceBeforeNs = _evidenceAfterNs = 0;
            if (!TryInterpolateTelemetryRotation(sourceNs, out var telemetry, out var reason))
            {
                bool beforeWasEvicted = _telemetryProvider.TelemetryOrientationHistory.Count > 0 &&
                    _telemetryProvider.TelemetryOrientationHistory[0].timestampNs > sourceNs;
                long newestBeforeNs = 0;
                foreach (var sample in _telemetryProvider.TelemetryOrientationHistory)
                    if (sample.hasOrientation && sample.timestampNs <= sourceNs) newestBeforeNs = sample.timestampNs;
                bool beforeTooOld = newestBeforeNs > 0 && sourceNs - newestBeforeNs > maximumTelemetryBracketAgeMilliseconds * 1e6;
                if (beforeTooOld)
                { FailReacquisitionEvidence("REACQUISITION_BRACKET_BEFORE_TOO_OLD", nowNs, e); return; }
                if (reason == "TELEMETRY_BRACKET_UNAVAILABLE" && !beforeWasEvicted)
                {
                    _evidenceState = "WAITING_FOR_BRACKET";
                    TraceReacquisitionEvidence(_evidenceState, reason, nowNs, e);
                    return;
                }
                FailReacquisitionEvidence(beforeWasEvicted ? "REACQUISITION_BRACKET_BEFORE_EVICTED" : reason, nowNs, e);
                return;
            }
            _evidenceBeforeNs = telemetry.beforeTimestampNs;
            _evidenceAfterNs = telemetry.afterTimestampNs;
            foreach (var sample in _telemetryProvider.TelemetryOrientationHistory)
                if ((sample.timestampNs == _evidenceBeforeNs || sample.timestampNs == _evidenceAfterNs) &&
                    !DjiValidatedTrackingCandidate.IsFiniteRotation(sample.navigationFromCameraRotation))
                { FailReacquisitionEvidence("REACQUISITION_ORIENTATION_INVALID", nowNs, e); return; }
            if (!DjiValidatedTrackingCandidate.IsFiniteRotation(telemetry.navigationFromCameraRotation) ||
                !DjiValidatedTrackingCandidate.IsFiniteRotation(e.metrics.cameraFromBoard.rotation))
            { FailReacquisitionEvidence("REACQUISITION_ORIENTATION_INVALID", nowNs, e); return; }

            var prediction = ReferenceFrameTransforms.PredictCameraFromBoardRotationOnly(
                _shadowRotationPropagationBaseline.cameraFromBoardVisual,
                _shadowRotationPropagationBaseline.navigationFromCameraTelemetry,
                telemetry.navigationFromCameraRotation);
            _evidencePrediction = prediction.rotation;
            bool rotationValid = DjiValidatedTrackingCandidate.IsRotationConsistent(prediction,
                e.metrics.cameraFromBoard, reacquisitionMaximumRotationDifferenceDegrees, out _evidenceAngle);
            if (!rotationValid)
            { FailReacquisitionEvidence("REACQUISITION_ORIENTATION_INCONSISTENT", nowNs, e); return; }
            if (!longOutage && Vector3.Distance(prediction.position, e.metrics.cameraFromBoard.position) > reacquisitionMaximumPositionDifferenceMeters)
            { FailReacquisitionEvidence("REACQUISITION_SHORT_POSITION_INCONSISTENT", nowNs, e); return; }
            if (longOutage && !HasLongLivedTelemetryLocalization()) return;

            _reacquisitionEvidence.Dequeue();
            if (_validatedEvidenceLastNs == 0 || sourceNs - _validatedEvidenceLastNs > pendingTelemetryBracketTimeoutSeconds * 1e9)
            {
                _validatedEvidenceStartNs = sourceNs;
                _validatedEvidenceCount = 0;
            }
            _validatedEvidenceLastNs = sourceNs;
            _validatedEvidenceCount++;
            _lastValidatedEvidence = e;
            _visualReacquisitionStartTimestampNs = _validatedEvidenceStartNs;
            _evidenceState = "VALIDATED_DWELL";
            TraceReacquisitionEvidence(_evidenceState, "ORIENTATION_VALIDATED", nowNs, e);
        }
        if (_validatedEvidenceCount == 0) return;
        if (nowNs - _lastValidatedEvidence.arrivalNs > pendingTelemetryBracketTimeoutSeconds * 1e9)
        { FailReacquisitionEvidence("REACQUISITION_VALIDATED_SEQUENCE_EXPIRED", nowNs, _lastValidatedEvidence); return; }
        if (_validatedEvidenceCount < 2 ||
            (_validatedEvidenceLastNs - _validatedEvidenceStartNs) / 1e9 < reacquisitionStableTimeSeconds) return;

        var accepted = _lastValidatedEvidence;
        TraceReacquisitionEvidence("ACCEPTED", "VALIDATED_SOURCE_DWELL_COMPLETE", nowNs, accepted);
        ClearVisualReacquisitionCandidate();
        _acceptedVisualReacquisitionMode = DjiOutageReacquisitionMode.PropagatedReacquisition;
        PromoteHighConfidenceVisualPose(accepted.result, accepted.metrics, accepted.logSnapshot, nowNs);
    }

    private void TraceReacquisitionEvidence(string state, string reason, long nowNs, PendingReacquisitionEvidence e)
    {
        if (_telemetryValidationLogger == null) return;
        _telemetryValidationLogger.RecordReacquisitionEvidence(nowNs, state, reason,
            e.metrics.timestampNs, e.result?.listenerGeneration ?? 0, e.metrics.markerCount,
            _reacquisitionEvidence.Count, _validatedEvidenceCount, _validatedEvidenceStartNs, _validatedEvidenceLastNs,
            e.metrics.cameraFromBoard.rotation, _evidencePrediction, _evidenceAngle, e.metrics.reprojectionRmsPixels,
            _evidenceBeforeNs, _evidenceAfterNs,
            _telemetryProvider != null && _telemetryProvider.TelemetryOrientationHistory.Count > 0
                ? _telemetryProvider.TelemetryOrientationHistory[_telemetryProvider.TelemetryOrientationHistory.Count - 1].timestampNs : 0);
    }

    private void TraceCandidateAppliedOrientation(Pose pose)
    {
        if (_telemetryValidationLogger == null) return;
        var history = _telemetryProvider?.TelemetryOrientationHistory;
        var latest = history != null && history.Count > 0 ? history[history.Count - 1] : default;
        _telemetryValidationLogger.RecordAppliedOrientation(DjiBoardVisionBridge.GetMonotonicTimestampNs(),
            CurrentRenderPoseSource, _currentHighConfidenceVisualTimestampNs, CurrentHighConfidenceCameraFromBoard.rotation,
            pose.rotation, latest.navigationFromCameraRotation, latest.timestampNs,
            _telemetryProvider?.LatestAircraftAttitudeArrivalTimestampNs ?? 0,
            _telemetryProvider?.LatestGimbalAttitudeArrivalTimestampNs ?? 0);
    }
}
