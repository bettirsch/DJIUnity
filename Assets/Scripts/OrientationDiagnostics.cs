using System;
using UnityEngine;

/// <summary>Owned provenance of a production-composed T_navigation_camera history sample.
/// Times are callback arrivals, not DJI measurement/exposure times.</summary>
public readonly struct OrientationTelemetryProvenance
{
    public readonly bool available;
    public readonly long aircraftArrivalNs, gimbalArrivalNs, wallClockMs;
    public readonly double aircraftPitch, aircraftRoll, aircraftYaw, gimbalPitch, gimbalRoll, gimbalYaw;
    public readonly bool hasRelativeGimbalYaw;
    public readonly double relativeGimbalYaw;
    public readonly Quaternion navigationFromAircraft, aircraftFromGimbal, gimbalFromCamera;

    public OrientationTelemetryProvenance(DJIPoseSnapshot s, Quaternion aircraft, Quaternion gimbal, Quaternion camera)
    {
        available = s != null && s.aircraft != null && s.gimbal != null;
        aircraftArrivalNs = s?.aircraftAttitudeArrivalTimestampNs ?? 0;
        gimbalArrivalNs = s?.gimbalAttitudeArrivalTimestampNs ?? 0;
        wallClockMs = s?.telemetryWallClockTimestampMs ?? 0;
        aircraftPitch = s?.aircraft?.pitch ?? double.NaN;
        aircraftRoll = s?.aircraft?.roll ?? double.NaN;
        aircraftYaw = s?.aircraft?.yaw ?? double.NaN;
        gimbalPitch = s?.gimbal?.pitch ?? double.NaN;
        gimbalRoll = s?.gimbal?.roll ?? double.NaN;
        gimbalYaw = s?.gimbal?.yaw ?? double.NaN;
        hasRelativeGimbalYaw = s?.gimbal?.hasYawRelativeToAircraftHeading ?? false;
        relativeGimbalYaw = s?.gimbal?.yawRelativeToAircraftHeading ?? double.NaN;
        navigationFromAircraft = aircraft; aircraftFromGimbal = gimbal; gimbalFromCamera = camera;
    }
}

/// <summary>Diagnostic-only transfer. Never read by admission, propagation or rendering.</summary>
public sealed class OrientationDiagnosticRecord
{
    public string recordType, purpose, outcome, reason, renderAuthority, visualQuality;
    public long candidateId, nativeFrameId, imageTimeNs, arrivalTimeNs, processingStartNs, processingEndNs;
    public long detectorEndNs, frameArrivalNs;
    public long eventTimeNs, applicationTimeNs, referenceId, previousReferenceId, referenceCreationNs;
    public int[] tagIds, previousTagIds;
    public float[] decisionMargins;
    public int nativeStatus, markerCount;
    public float rms, maxResidual;
    public float normalizedRms, normalizedCornerResidual, borderDistancePixels;
    public bool poseValid, propagationActive, selectedValid, referenceValid, previousReferenceValid;
    public bool hasQualityMetrics;
    public Quaternion candidate, selected, reference, previousReference;
    public DjiCameraPoseProvider.TelemetryOrientationSample? endpointA, endpointB;
    public float alpha;
    public bool hasBorderExperimentDiagnostics, borderExperimentEnabled, borderReacquisition, borderEligible, borderMetricsAvailable;
    public float activeBorderThresholdPixels, candidateTranslationJumpMeters, candidateRotationJumpDegrees;
    public int qualityConfirmationCount, qualityConfirmationRequired;
    public double reacquisitionDwellMilliseconds, requiredReacquisitionDwellMilliseconds;
    public Vector3 candidatePosition;
    public bool hasBorderRetentionDiagnostics, borderRetentionEnabled, borderRetentionMetricsAvailable;
    public bool borderRetentionEligible, borderRetentionPreviousHighConfidence, borderRetentionLegacy64Eligible;
    public bool borderRetentionCandidatePromoted;
    public float borderRetentionThresholdPixels;
    public string borderRetentionAuthorityAtEvaluation;
    public bool hasStaleJumpDiagnostics, staleJumpExperimentEnabled, staleJumpMetricsAvailable;
    public bool legacyAbsoluteJumpRejected, appliedCorrectionAvailable;
    public double previousValidVisualAgeMs, previousAcceptedVisualAgeMs;
    public float staleJumpTranslationM;
    public int staleJumpConfirmationCount;
    public string legacyTranslationJumpDecision, experimentalTranslationJumpDecision, staleJumpReason, previousAppliedSource;
    public long previousAppliedTimestampNs;
    public Vector3 previousAppliedCameraInBoard, appliedCameraInBoard;

    public static bool IsUsableQuaternion(Quaternion q) =>
        float.IsFinite(q.x) && float.IsFinite(q.y) && float.IsFinite(q.z) && float.IsFinite(q.w) &&
        q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w > 0.5f;
}
