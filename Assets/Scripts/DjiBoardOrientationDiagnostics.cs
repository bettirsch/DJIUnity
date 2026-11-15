using System;
using System.Collections.Generic;
using UnityEngine;

public sealed partial class DjiBoardVisionProvider
{
    // Audit state only. No production decision reads these fields.
    private string _orientationDiagnosticQualityReason;
    private int[] _orientationPreviousTagIds;
    private long _orientationReferenceCreationNs;
    private bool _orientationPromoted;
    private VisualPoseMetrics _orientationMetrics;
    private readonly Dictionary<string, string> _orientationBracketSignatures = new();
    private readonly Queue<string> _orientationBracketKeys = new();

    private OrientationDiagnosticRecord OrientationRecord(string type, NativeBoardResult r = null)
    {
        return new OrientationDiagnosticRecord
        {
            recordType = type,
            eventTimeNs = DjiBoardVisionBridge.GetMonotonicTimestampNs(),
            candidateId = r == null ? _currentHighConfidenceVisualTimestampNs : GetVisionTimestamp(r),
            nativeFrameId = r?.visionFrameId ?? 0,
            imageTimeNs = r == null ? _currentHighConfidenceVisualTimestampNs : GetVisionTimestamp(r),
            processingStartNs = r?.detectorStartTimestampNs ?? 0,
            processingEndNs = r?.pnpCompletedTimestampNs ?? 0,
            detectorEndNs = r?.detectorEndTimestampNs ?? 0,
            frameArrivalNs = r?.frameArrivalTimestampNs ?? 0,
            markerCount = r?.markerCount ?? 0,
            tagIds = r?.markerIds,
            previousTagIds = _orientationPreviousTagIds,
            decisionMargins = r?.markerDecisionMargins,
            nativeStatus = r?.status ?? 0,
            rms = r?.reprojectionRms ?? float.NaN,
            maxResidual = r?.maxResidual ?? float.NaN,
            renderAuthority = CurrentRenderPoseSource.ToString(),
            visualQuality = CurrentVisualPoseQuality.ToString(),
            propagationActive = _shadowPropagationActive,
            referenceId = _lastAuthoritativeVisualPose.timestampNs,
            reference = _lastAuthoritativeVisualPose.cameraFromBoard.rotation,
            referenceValid = _lastAuthoritativeVisualPose.IsValid,
            referenceCreationNs = _orientationReferenceCreationNs
        };
    }

    private void BeginOrientationCandidate(NativeBoardResult result, long arrival)
    {
        _orientationDiagnosticQualityReason = null;
        _orientationPromoted = false;
        _orientationMetrics = default;
#if DJI_APRILTAG_BORDER_48_EXPERIMENT
        BeginBorderExperimentDiagnostic(result);
#endif
    }

    private void EndOrientationCandidate(NativeBoardResult result, long arrival)
    {
        try
        {
            if (_telemetryValidationLogger == null) return;
            var r = OrientationRecord("VisualOrientationCandidate", result);
            r.arrivalTimeNs = arrival;
            r.poseValid = TryGetCameraFromBoard(result, out var pose) &&
                          OrientationDiagnosticRecord.IsUsableQuaternion(pose.rotation);
            r.candidate = pose.rotation;
            r.outcome = !r.poseValid ? (result.status == 2 ? "INVALID_POSE" : "NO_POSE") :
                _orientationPromoted ? "PROMOTED_HIGH_CONFIDENCE" : "VALID_POSE_REJECTED";
            r.reason = _orientationDiagnosticQualityReason;
            if (string.IsNullOrEmpty(r.reason) && !_orientationPromoted)
                r.reason = _hasVisualReacquisitionCandidate ? "REACQUISITION_PENDING" :
                    _holdingVisualAuthorityForBorderlineQuality ? "BORDERLINE_AUTHORITY_HOLD" :
                    _holdingVisualAuthorityForTranslationJumpSuspect ? "TRANSLATION_JUMP_SUSPECT_HOLD" :
                    result.status != 2 ? result.rejectionReason : "QUALITY_CONFIRMATION_NOT_PROMOTED";
            r.normalizedRms = _orientationMetrics.normalizedReprojectionRms;
            r.hasQualityMetrics = _orientationMetrics.timestampNs == r.imageTimeNs && _orientationMetrics.markerCount > 0;
            r.normalizedCornerResidual = _orientationMetrics.normalizedMaxCornerResidual;
            r.borderDistancePixels = _orientationMetrics.minimumBorderDistancePixels;
#if DJI_APRILTAG_BORDER_48_EXPERIMENT
            FillBorderExperimentDiagnostic(r, pose);
#endif
#if DJI_APRILTAG_STALE_JUMP_EXPERIMENT
            FillStaleJumpDiagnostic(r);
#endif
#if DJI_APRILTAG_BORDER_RETENTION_EXPERIMENT || DJI_APRILTAG_BORDER_48_EXPERIMENT || DJI_APRILTAG_STALE_JUMP_EXPERIMENT
            FillBorderRetentionDiagnostic(r);
#endif
            _telemetryValidationLogger.WriteOrientationDiagnostic(r);
            _orientationPreviousTagIds = result.markerIds;
        }
        catch (Exception) { /* Observability must never change a pose decision. */ }
    }

    private void TraceOrientationPromotion(NativeBoardResult result, Pose pose, long arrival)
    {
        _orientationPromoted = true;
        try
        {
            var r = OrientationRecord("VisualCandidatePromoted", result);
            r.poseValid = true; r.candidate = pose.rotation; r.arrivalTimeNs = arrival;
            r.outcome = "PROMOTED_HIGH_CONFIDENCE";
            r.reason = "EXISTING_PRODUCTION_ADMISSION";
#if DJI_APRILTAG_BORDER_48_EXPERIMENT
            FillBorderExperimentDiagnostic(r, pose);
#endif
#if DJI_APRILTAG_STALE_JUMP_EXPERIMENT
            FillStaleJumpDiagnostic(r);
#endif
#if DJI_APRILTAG_BORDER_RETENTION_EXPERIMENT || DJI_APRILTAG_BORDER_48_EXPERIMENT || DJI_APRILTAG_STALE_JUMP_EXPERIMENT
            FillBorderRetentionDiagnostic(r);
#endif
            _telemetryValidationLogger?.WriteOrientationDiagnostic(r);
        }
        catch (Exception) { }
    }

    private void TraceOrientationReference()
    {
        if (_lastAuthoritativeVisualPose.timestampNs == _currentHighConfidenceVisualTimestampNs) return;
        try
        {
            var r = OrientationRecord("VisualTrustedReferenceTrace");
            if (!_lastAuthoritativeVisualPose.IsValid) _orientationReferenceCreationNs = r.eventTimeNs;
            r.previousReferenceId = _lastAuthoritativeVisualPose.timestampNs;
            r.previousReferenceValid = _lastAuthoritativeVisualPose.IsValid;
            r.previousReference = _lastAuthoritativeVisualPose.cameraFromBoard.rotation;
            r.referenceId = _currentHighConfidenceVisualTimestampNs;
            r.reference = CurrentHighConfidenceCameraFromBoard.rotation;
            r.referenceValid = true;
            r.referenceCreationNs = _orientationReferenceCreationNs;
            r.reason = "AUTHORITATIVE_VISUAL_RENDER_COMMIT_NOT_NEW_FILTER";
            _telemetryValidationLogger?.WriteOrientationDiagnostic(r);
        }
        catch (Exception) { }
    }

    private void TraceOrientationAuthorityTransition(DjiRenderPoseSource previous, DjiRenderPoseSource current)
    {
        try
        {
            var r = OrientationRecord("VisualRenderAuthorityTrace");
            r.purpose = previous + "->" + current;
            r.applicationTimeNs = r.eventTimeNs;
            r.reason = "EXISTING_RENDER_SOURCE_COMMIT";
            _telemetryValidationLogger?.WriteOrientationDiagnostic(r);
        }
        catch (Exception) { }
    }

    private void TraceOrientationBracket(long target, string purpose,
        DjiCameraPoseProvider.TelemetryOrientationSample? before,
        DjiCameraPoseProvider.TelemetryOrientationSample? after,
        float alpha, Quaternion selected, bool valid, string reason)
    {
        try
        {
            var key = purpose + ":" + target;
            var signature = (before?.timestampNs ?? 0) + ":" + (after?.timestampNs ?? 0) + ":" + valid;
            if (_orientationBracketSignatures.TryGetValue(key, out var previous) && previous == signature) return;
            if (!_orientationBracketSignatures.ContainsKey(key)) _orientationBracketKeys.Enqueue(key);
            _orientationBracketSignatures[key] = signature;
            while (_orientationBracketKeys.Count > 160)
                _orientationBracketSignatures.Remove(_orientationBracketKeys.Dequeue());
            var r = OrientationRecord("TelemetryOrientationBracket");
            r.candidateId = target; r.imageTimeNs = target; r.purpose = purpose;
            r.endpointA = before; r.endpointB = after; r.alpha = alpha;
            r.selected = selected; r.selectedValid = valid; r.reason = reason;
            _telemetryValidationLogger?.WriteOrientationDiagnostic(r);
        }
        catch (Exception) { }
    }
}
