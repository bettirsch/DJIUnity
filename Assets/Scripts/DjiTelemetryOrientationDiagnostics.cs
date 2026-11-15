using System;
using System.Diagnostics;
using UnityEngine;

public sealed partial class DjiTelemetryValidationLogger
{
    public void WriteOrientationDiagnostic(OrientationDiagnosticRecord record)
    {
        if (_visualLatencyWriter == null || !isActiveAndEnabled) return;
        try
        {
            var start = Stopwatch.GetTimestamp();
            _visualLatencyWriter.WriteLine(BuildOrientationDiagnosticJson(_telemetrySessionId, record));
            if (_visualLatencyWriter is BoundedDiagnosticJsonlWriter buffered)
                buffered.ObserveSerialization(Stopwatch.GetTimestamp() - start);
        }
        catch (Exception)
        {
            if (_visualLatencyWriter is BoundedDiagnosticJsonlWriter buffered) buffered.ReportCaptureError();
        }
    }

    public static string BuildOrientationDiagnosticJson(string session, OrientationDiagnosticRecord r)
    {
        var j = new JsonLine().BeginObject();
        j.Property("recordType").String(r.recordType ?? "OrientationDiagnosticUnavailable");
        j.Property("sessionId").String(session ?? "UNAVAILABLE");
        j.Property("schemaVersion").Number(1);
        j.Property("clockDomain").String("ANDROID_MONOTONIC_elapsedRealtimeNanos");
        j.Property("timestampUnit").String("ns");
        j.Property("imageTimestampSemantics").String("CAMERA_FRAME_LISTENER_CALLBACK_ARRIVAL_NOT_EXPOSURE");
        j.Property("telemetryTimestampSemantics").String("KEYMANAGER_CALLBACK_ARRIVAL_NOT_FC_CAPTURE");
        j.Property("clockMapping").String("SAME_ANDROID_CLOCK_NO_EXPOSURE_OR_FC_SYNC");
        j.Property("quaternionOrder").String("XYZW");
        j.Property("candidateFrame").String("T_camera_board_Board_to_OpenCV_OpticalCamera");
        j.Property("telemetryFrame").String("T_navigation_camera_OpticalCamera_to_local_NED");
        j.Property("composition").String("inverse(currentNavigationFromCamera)*referenceNavigationFromCamera*referenceCameraFromBoard");
        j.Property("eventMonotonicTime").Number(r.eventTimeNs);
        j.Property("candidateId").Number(r.candidateId);
        j.Property("nativeFrameId").Number(r.nativeFrameId);
        j.Property("imageMeasurementTime").Number(r.imageTimeNs);
        j.Property("candidateArrivalTime").Number(r.arrivalTimeNs);
        j.Property("cpuFrameArrivalTime").Number(r.frameArrivalNs);
        j.Property("detectorStartTime").Number(r.processingStartNs);
        j.Property("detectorEndTime").Number(r.detectorEndNs);
        j.Property("candidateProcessingTime").Number(r.processingEndNs);
        j.Property("candidateApplicationTime").NullableNumber(r.applicationTimeNs > 0 ? r.applicationTimeNs : (long?)null);
        j.Property("purpose").NullableString(r.purpose);
        j.Property("detectedTagCount").Number(r.markerCount);
        j.Property("detectedTagIds").String(r.tagIds == null ? "" : string.Join(",", r.tagIds));
        j.Property("previousTagIds").String(r.previousTagIds == null ? "" : string.Join(",", r.previousTagIds));
        j.Property("markerDecisionMargins").String(r.decisionMargins == null ? "" : string.Join(",", Array.ConvertAll(r.decisionMargins, x => x.ToString("R", System.Globalization.CultureInfo.InvariantCulture))));
        j.Property("nativeStatus").Number(r.nativeStatus);
        j.Property("reprojectionRms").Number(r.rms);
        j.Property("maxCornerResidual").Number(r.maxResidual);
        j.Property("qualityMetricsAvailable").Boolean(r.hasQualityMetrics);
        j.Property("normalizedRms").Number(r.hasQualityMetrics ? r.normalizedRms : float.NaN);
        j.Property("normalizedCornerResidual").Number(r.hasQualityMetrics ? r.normalizedCornerResidual : float.NaN);
        j.Property("minimumBorderDistancePixels").Number(r.hasQualityMetrics ? r.borderDistancePixels : float.NaN);
        j.Property("visualQuality").NullableString(r.visualQuality);
        j.Property("originalAdmissionOutcome").NullableString(r.outcome);
        j.Property("originalRejectionReason").NullableString(r.reason);
        j.Property("renderAuthority").NullableString(r.renderAuthority);
        j.Property("propagationActive").Boolean(r.propagationActive);
        j.Property("poseValid").Boolean(r.poseValid);
        if (r.hasBorderExperimentDiagnostics)
        {
            j.Property("aprilTagBorder48ExperimentEnabled").Boolean(r.borderExperimentEnabled);
            j.Property("borderEvaluationIsReacquisition").Boolean(r.borderReacquisition);
            j.Property("activeBorderThresholdPx").Number(r.activeBorderThresholdPixels);
            j.Property("borderEligible").NullableBoolean(r.borderMetricsAvailable ? r.borderEligible : (bool?)null);
            j.Property("qualityConfirmationCount").Number(r.qualityConfirmationCount);
            j.Property("qualityConfirmationRequired").Number(r.qualityConfirmationRequired);
            j.Property("reacquisitionDwellMs").Number(r.reacquisitionDwellMilliseconds);
            j.Property("reacquisitionRequiredDwellMs").Number(r.requiredReacquisitionDwellMilliseconds);
            j.Property("candidateTranslationJumpM").NullableNumber(r.borderMetricsAvailable ? r.candidateTranslationJumpMeters : (float?)null);
            j.Property("candidateRotationJumpDeg").NullableNumber(r.borderMetricsAvailable ? r.candidateRotationJumpDegrees : (float?)null);
            j.Property("candidateCameraFromBoardPosition").BeginObject();
            j.Property("x").NullableNumber(r.poseValid ? r.candidatePosition.x : (float?)null);
            j.Property("y").NullableNumber(r.poseValid ? r.candidatePosition.y : (float?)null);
            j.Property("z").NullableNumber(r.poseValid ? r.candidatePosition.z : (float?)null);
            j.EndObject();
        }
        if (r.hasBorderRetentionDiagnostics)
        {
            j.Property("aprilTagBorderRetentionExperimentEnabled").Boolean(r.borderRetentionEnabled);
            j.Property("borderRetentionScopeApplied").NullableBoolean(
                r.borderRetentionMetricsAvailable ? r.borderRetentionEligible : (bool?)null);
            j.Property("highConfidenceBeforeEvaluation").NullableBoolean(
                r.borderRetentionMetricsAvailable ? r.borderRetentionPreviousHighConfidence : (bool?)null);
            j.Property("renderAuthorityAtEvaluation").NullableString(
                r.borderRetentionMetricsAvailable ? r.borderRetentionAuthorityAtEvaluation : null);
            j.Property("legacy64BorderEligible").NullableBoolean(
                r.borderRetentionMetricsAvailable ? r.borderRetentionLegacy64Eligible : (bool?)null);
            if (!r.hasBorderExperimentDiagnostics)
                j.Property("activeBorderThresholdPx").NullableNumber(
                    r.borderRetentionMetricsAvailable ? r.borderRetentionThresholdPixels : (float?)null);
            j.Property("newVisualPosePromoted").Boolean(r.borderRetentionCandidatePromoted);
            // Candidate/promotion is not proof of a later render application.
            j.Property("newVisualPoseApplied").NullableBoolean(null);
        }
        if (r.hasStaleJumpDiagnostics)
        {
            j.Property("aprilTagStaleJumpExperimentEnabled").Boolean(r.staleJumpExperimentEnabled);
            j.Property("previousValidVisualObservationAgeMs").NullableNumber(
                r.staleJumpMetricsAvailable && r.previousValidVisualAgeMs > 0 ? r.previousValidVisualAgeMs : (double?)null);
            j.Property("previousAcceptedVisualPoseAgeMs").NullableNumber(
                r.staleJumpMetricsAvailable ? r.previousAcceptedVisualAgeMs : (double?)null);
            j.Property("staleComparisonTranslationDifferenceM").NullableNumber(
                r.staleJumpMetricsAvailable ? r.staleJumpTranslationM : (float?)null);
            j.Property("legacyAbsoluteTranslationJumpRejected").NullableBoolean(
                r.staleJumpMetricsAvailable ? r.legacyAbsoluteJumpRejected : (bool?)null);
            j.Property("legacyTranslationJumpDecision").NullableString(
                r.staleJumpMetricsAvailable ? r.legacyTranslationJumpDecision : null);
            j.Property("experimentalTranslationJumpDecision").NullableString(
                r.staleJumpMetricsAvailable ? r.experimentalTranslationJumpDecision : null);
            j.Property("staleClusterConfirmationCount").Number(r.staleJumpConfirmationCount);
            j.Property("staleClusterReason").NullableString(r.staleJumpMetricsAvailable ? r.staleJumpReason : null);
            if (!r.hasBorderExperimentDiagnostics)
            {
                j.Property("qualityConfirmationCount").Number(r.qualityConfirmationCount);
                j.Property("qualityConfirmationRequired").Number(r.qualityConfirmationRequired);
            }
            if (r.appliedCorrectionAvailable)
            {
                j.Property("positionFrame").String("BOARD_EQUALS_PERSISTENT_WORLD_NOT_UNITY_RENDER_SPACE");
                j.Property("previousApplicationTimestampNs").Number(r.previousAppliedTimestampNs);
                j.Property("previousRenderAuthority").NullableString(r.previousAppliedSource);
                var correction = r.appliedCameraInBoard - r.previousAppliedCameraInBoard;
                WriteStaleJumpPosition(j, "previousAppliedCameraInBoard", r.previousAppliedCameraInBoard);
                WriteStaleJumpPosition(j, "appliedCameraInBoard", r.appliedCameraInBoard);
                j.Property("appliedPositionCorrectionM").Number(correction.magnitude);
            }
        }
        WriteOrientationQuaternion(j, "candidateQuaternion", r.poseValid, r.candidate);
        if (r.recordType == "TelemetryOrientationBracket" || r.endpointA.HasValue || r.endpointB.HasValue)
        {
            j.Property("bracketStatus").String(r.selectedValid ? "EXACT_BRACKET" :
                r.endpointA.HasValue && r.endpointB.HasValue ? "STALE" : "UNAVAILABLE");
            WriteOrientationEndpoint(j, "endpointA", r.endpointA);
            WriteOrientationEndpoint(j, "endpointB", r.endpointB);
            j.Property("interpolationFactor").NullableNumber(r.selectedValid ? r.alpha : (float?)null);
            j.Property("selectedValid").Boolean(r.selectedValid);
            WriteOrientationQuaternion(j, "actualSelectedOrientation", r.selectedValid, r.selected);
        }
        j.Property("referenceId").Number(r.referenceId);
        j.Property("previousReferenceId").Number(r.previousReferenceId);
        j.Property("referenceCreationTime").Number(r.referenceCreationNs);
        j.Property("referenceMeasurementTime").Number(r.referenceId);
        j.Property("referenceUpdateTime").Number(r.eventTimeNs);
        j.Property("referenceAgeMs").NullableNumber(r.referenceId > 0 ? (r.eventTimeNs - r.referenceId) / 1e6 : (double?)null);
        j.Property("referenceFrame").String("T_camera_board_Board_to_OpenCV_OpticalCamera");
        j.Property("referenceValidity").Boolean(r.referenceValid);
        WriteOrientationQuaternion(j, "newReferenceQuaternion", r.referenceValid, r.reference);
        WriteOrientationQuaternion(j, "previousReferenceQuaternion", r.previousReferenceValid, r.previousReference);
        return j.EndObject().ToString();
    }

    private static void WriteStaleJumpPosition(JsonLine j, string name, Vector3 position)
    {
        j.Property(name).BeginObject();
        j.Property("x").Number(position.x);
        j.Property("y").Number(position.y);
        j.Property("z").Number(position.z);
        j.EndObject();
    }

    private static void WriteOrientationQuaternion(JsonLine j, string name, bool valid, Quaternion q)
    {
        j.Property(name).BeginObject();
        j.Property("x").NullableNumber(valid ? q.x : (float?)null);
        j.Property("y").NullableNumber(valid ? q.y : (float?)null);
        j.Property("z").NullableNumber(valid ? q.z : (float?)null);
        j.Property("w").NullableNumber(valid ? q.w : (float?)null);
        j.EndObject();
    }

    private static void WriteOrientationEndpoint(JsonLine j, string name, DjiCameraPoseProvider.TelemetryOrientationSample? s)
    {
        j.Property(name).BeginObject();
        j.Property("present").Boolean(s.HasValue);
        j.Property("valid").Boolean(s.HasValue && s.Value.hasOrientation);
        j.Property("timestampNs").NullableNumber(s.HasValue ? s.Value.timestampNs : (long?)null);
        j.Property("frame").String("T_navigation_camera_OpticalCamera_to_local_NED");
        WriteOrientationQuaternion(j, "quaternion", s.HasValue && s.Value.hasOrientation, s.GetValueOrDefault().navigationFromCameraRotation);
        var p = s.GetValueOrDefault().provenance;
        j.Property("provenanceAvailable").Boolean(p.available);
        j.Property("aircraftArrivalNs").NullableNumber(p.available ? p.aircraftArrivalNs : (long?)null);
        j.Property("gimbalArrivalNs").NullableNumber(p.available ? p.gimbalArrivalNs : (long?)null);
        j.Property("wallClockMsDiagnosticOnly").NullableNumber(p.available ? p.wallClockMs : (long?)null);
        j.Property("aircraftPitchDeg").Number(p.available ? p.aircraftPitch : double.NaN);
        j.Property("aircraftRollDeg").Number(p.available ? p.aircraftRoll : double.NaN);
        j.Property("aircraftYawDeg").Number(p.available ? p.aircraftYaw : double.NaN);
        j.Property("gimbalPitchDeg").Number(p.available ? p.gimbalPitch : double.NaN);
        j.Property("gimbalRollDeg").Number(p.available ? p.gimbalRoll : double.NaN);
        j.Property("gimbalYawDeg").Number(p.available ? p.gimbalYaw : double.NaN);
        j.Property("gimbalYawRelativeToAircraftDiagnosticOnlyDeg").Number(p.hasRelativeGimbalYaw ? p.relativeGimbalYaw : double.NaN);
        j.Property("cameraComposition").String("navigationFromAircraft*aircraftFromGimbal*gimbalFromCamera;gimbalAttitudeIsNavigationReferenced;relativeYawNotComposed");
        WriteOrientationQuaternion(j, "navigationFromAircraft", p.available, p.navigationFromAircraft);
        WriteOrientationQuaternion(j, "aircraftFromGimbal", p.available, p.aircraftFromGimbal);
        WriteOrientationQuaternion(j, "gimbalFromCamera", p.available, p.gimbalFromCamera);
        j.EndObject();
    }
}
