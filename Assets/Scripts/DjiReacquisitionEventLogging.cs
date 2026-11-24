using System;
using UnityEngine;

public sealed partial class DjiTelemetryValidationLogger
{
    private long _nextEvidenceEventNs, _lastAppliedOrientationEventNs;
    private int _evidenceWaitCount, _evidenceInvalidCount, _evidenceValidCount;
    private bool _hasAppliedOrientationEvent;
    private DjiRenderPoseSource _appliedOrientationEventSource;
    private Quaternion _appliedOrientationEventRotation;

    private void ResetReacquisitionEventLogging()
    {
        _nextEvidenceEventNs = _lastAppliedOrientationEventNs = 0;
        _evidenceWaitCount = _evidenceInvalidCount = _evidenceValidCount = 0;
        _hasAppliedOrientationEvent = false;
    }

    // Rate-limit before constructing JSON. Repeated callback checks are
    // counters, not extra validation/dwell credit and not per-frame file IO.
    public void RecordReacquisitionEvidence(long nowNs, string state, string reason, long imageNs,
        long generation, int markerCount, int queued, int validatedCount, long startNs, long lastNs,
        Quaternion visual, Quaternion predicted, float residual, float rms, long beforeNs, long afterNs, long latestTelemetryNs)
    {
        var writer = _normalWriter ?? _writer;
        if (writer == null) return;
        try
        {
            if (state == "WAITING_FOR_BRACKET") _evidenceWaitCount++;
            else if (state == "INVALID_OR_EXPIRED_EVIDENCE") _evidenceInvalidCount++;
            else if (state == "VALIDATED_DWELL") _evidenceValidCount++;
            if (nowNs < _nextEvidenceEventNs && state != "ACCEPTED") return;
            var j = NormalEvent("ReacquisitionEvidence", nowNs)
                .Property("state").String(state).Property("reason").String(reason)
                .Property("imageSourceTimestampNs").Number(imageNs).Property("listenerGeneration").Number(generation)
                .Property("markerCount").Number(markerCount).Property("rmsPx").NullableNumber(float.IsFinite(rms) ? rms : (float?)null)
                .Property("queued").Number(queued).Property("validatedCount").Number(validatedCount)
                .Property("validatedStartNs").Number(startNs).Property("validatedLastNs").Number(lastNs)
                .Property("beforeNs").Number(beforeNs).Property("afterNs").Number(afterNs)
                .Property("latestTelemetryNs").Number(latestTelemetryNs)
                .Property("angularResidualDeg").NullableNumber(float.IsFinite(residual) ? residual : (float?)null)
                .Property("waitChecksSinceEvent").Number(_evidenceWaitCount)
                .Property("invalidChecksSinceEvent").Number(_evidenceInvalidCount)
                .Property("validatedSamplesSinceEvent").Number(_evidenceValidCount);
            WriteEvidenceRotation(j, "visualCameraFromBoardRotation", visual);
            WriteEvidenceRotation(j, "predictedCameraFromBoardRotation", predicted);
            writer.WriteLine(j.EndObject().ToString());
            _evidenceWaitCount = _evidenceInvalidCount = _evidenceValidCount = 0;
            _nextEvidenceEventNs = nowNs + 1_000_000_000L;
        }
        catch (Exception) { writer.ReportCaptureError(); }
    }

    public void RecordAppliedOrientation(long nowNs, DjiRenderPoseSource source, long visualNs,
        Quaternion acceptedVisual, Quaternion applied, Quaternion telemetry, long telemetryNs,
        long aircraftNs, long gimbalNs)
    {
        var writer = _normalWriter ?? _writer;
        if (writer == null) return;
        try
        {
            bool transition = !_hasAppliedOrientationEvent || source != _appliedOrientationEventSource;
            float delta = _hasAppliedOrientationEvent ? Quaternion.Angle(_appliedOrientationEventRotation, applied) : 0f;
            if (!transition && (nowNs - _lastAppliedOrientationEventNs < 1_000_000_000L || delta < 6f)) return;
            var j = NormalEvent("AppliedOrientationEvent", nowNs)
                .Property("renderSource").String(source.ToString()).Property("visualSourceTimestampNs").Number(visualNs)
                .Property("telemetrySourceTimestampNs").Number(telemetryNs)
                .Property("aircraftAgeMs").NullableNumber(aircraftNs > 0 && nowNs >= aircraftNs ? (nowNs-aircraftNs)/1e6 : (double?)null)
                .Property("gimbalAgeMs").NullableNumber(gimbalNs > 0 && nowNs >= gimbalNs ? (nowNs-gimbalNs)/1e6 : (double?)null)
                .Property("deltaSinceLastEventDeg").Number(delta).Property("sourceTransition").Boolean(transition);
            WriteEvidenceRotation(j, "acceptedVisualCameraFromBoardRotation", acceptedVisual);
            WriteEvidenceRotation(j, "appliedCameraFromBoardRotation", applied);
            WriteEvidenceRotation(j, "navigationFromCameraTelemetryRotation", telemetry);
            writer.WriteLine(j.EndObject().ToString());
            _hasAppliedOrientationEvent = true; _lastAppliedOrientationEventNs = nowNs;
            _appliedOrientationEventSource = source; _appliedOrientationEventRotation = applied;
        }
        catch (Exception) { writer.ReportCaptureError(); }
    }

    private static void WriteEvidenceRotation(JsonLine j, string name, Quaternion q)
    {
        j.Property(name).BeginObject();
        j.Property("x").NullableNumber(float.IsFinite(q.x) ? q.x : (float?)null);
        j.Property("y").NullableNumber(float.IsFinite(q.y) ? q.y : (float?)null);
        j.Property("z").NullableNumber(float.IsFinite(q.z) ? q.z : (float?)null);
        j.Property("w").NullableNumber(float.IsFinite(q.w) ? q.w : (float?)null);
        j.EndObject();
    }
}
