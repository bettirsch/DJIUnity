using System;
using UnityEngine;

public sealed partial class DjiTelemetryValidationLogger
{
    [SerializeField] private bool enableMotionDiagnostics = true;
    private HorizontalAdmissionTraceGate _motionTraceGate = new();
    private DjiWorldLocalizationShadow _motionTraceSource;
    private HorizontalAdmissionTrace _latestMotionTrace;
    private HorizontalAdmissionTrace _pendingMotionApplication;
    private bool _hasPendingMotionApplication;
    private long _lastMotionVisualTimestampNs;
    private long _nextMotionFlushNs;
    private bool MotionDiagnosticsReady => enableMotionDiagnostics && isActiveAndEnabled && _visualLatencyWriter != null;

    private void StartMotionDiagnostics()
    {
        if (!enableMotionDiagnostics) return;
        _motionTraceGate = new HorizontalAdmissionTraceGate();
        _lastMotionVisualTimestampNs = 0L;
        _nextOrientationTimingNs = 0L;
        _hasPendingMotionApplication = false;
        _motionTraceSource = FindFirstObjectByType<DjiWorldLocalizationShadow>();
        if (_motionTraceSource != null) _motionTraceSource.HorizontalAdmissionEvaluated += OnHorizontalAdmissionEvaluated;
        if (_boardVisionProvider != null) _boardVisionProvider.AuthoritativeVisualPoseAccepted += OnMotionVisualAccepted;
    }

    private void StopMotionDiagnostics()
    {
        if (_motionTraceSource != null) _motionTraceSource.HorizontalAdmissionEvaluated -= OnHorizontalAdmissionEvaluated;
        if (_boardVisionProvider != null) _boardVisionProvider.AuthoritativeVisualPoseAccepted -= OnMotionVisualAccepted;
        _motionTraceSource = null;
        _hasPendingMotionApplication = false;
    }

    private void OnApplicationPause(bool paused)
    {
        if (!paused) return;
        try { _normalWriter?.Flush(); _writer?.Flush(); _outageWriter?.Flush(); _visualLatencyWriter?.Flush(); }
        catch (Exception exception) { LogWriteFailure(exception); }
    }

    private void OnHorizontalAdmissionEvaluated(HorizontalAdmissionTrace trace)
    {
        if (!MotionDiagnosticsReady) return;
        try
        {
            if (_latestMotionTrace.baselineTimestampNs != trace.baselineTimestampNs) _hasPendingMotionApplication = false;
            _latestMotionTrace = trace;
            if (trace.current.estimate.targetAccepted &&
                trace.previous.acceptedGnssTarget != trace.current.estimate.acceptedGnssTarget)
            {
                _pendingMotionApplication = trace;
                _hasPendingMotionApplication = true;
            }
            if (_motionTraceGate.ShouldWrite(trace))
                WriteMotionJson(BuildHorizontalAdmissionTraceJson(_telemetrySessionId, trace), trace.monotonicTime);
        }
        catch (Exception exception) { LogWriteFailure(exception); }
    }

    public void RecordMotionPoseApplied(long timestampNs, DjiRenderPoseSource source, Pose pose)
    {
        RecordCameraOrientationTiming(timestampNs, source, pose);
        if (!MotionDiagnosticsReady || !_hasPendingMotionApplication || !IsMotionPropagation(source)) return;
        try
        {
            if (_latestMotionTrace.baselineTimestampNs != _pendingMotionApplication.baselineTimestampNs ||
                _latestMotionTrace.current.estimate.acceptedGnssTargetTimestampNs !=
                _pendingMotionApplication.current.estimate.acceptedGnssTargetTimestampNs)
            {
                _hasPendingMotionApplication = false;
                return;
            }
            if (Vector2.Distance(_latestMotionTrace.current.estimate.smoothedNavigationDelta,
                    _pendingMotionApplication.previous.smoothedNavigationDelta) <= 0.0000001f) return;
            WriteMotionJson(BuildHorizontalAdmissionTraceJson(_telemetrySessionId, _latestMotionTrace,
                "FirstOutputApplied", timestampNs, pose, _pendingMotionApplication.monotonicTime), timestampNs, true);
            _hasPendingMotionApplication = false;
        }
        catch (Exception exception) { LogWriteFailure(exception); }
    }

    public void RecordMotionRenderSourceTransition(long timestampNs, DjiRenderPoseSource previous, DjiRenderPoseSource current)
    {
        RecordNormalRenderTransition(timestampNs, previous, current);
        if (!MotionDiagnosticsReady || previous == current) return;
        try
        {
            var type = !IsMotionPropagation(previous) && IsMotionPropagation(current) ? "PropagationStarted" :
                IsMotionPropagation(previous) && !IsMotionPropagation(current) ? "PropagationStopped" : "RenderSourceTransition";
            WriteMotionJson(BuildMotionEventJson(type, _telemetrySessionId, timestampNs, current.ToString(), previous.ToString()), timestampNs, true);
            if (current == DjiRenderPoseSource.Visual && IsMotionPropagation(previous))
                WriteMotionJson(BuildMotionEventJson("VisualReacquisition", _telemetrySessionId, timestampNs,
                    current.ToString(), previous.ToString()), timestampNs, true);
            if (!IsMotionPropagation(current)) _hasPendingMotionApplication = false;
        }
        catch (Exception exception) { LogWriteFailure(exception); }
    }

    private void OnMotionVisualAccepted(Pose pose, long sourceTimestampNs)
    {
        if (!MotionDiagnosticsReady || sourceTimestampNs <= _lastMotionVisualTimestampNs) return;
        try
        {
            var now = DjiBoardVisionBridge.GetMonotonicTimestampNs();
            var json = MotionEventHeader("AuthoritativeVisualPoseAccepted", _telemetrySessionId, now);
            json.Property("visualSourceTimestampNs").Number(sourceTimestampNs);
            WriteTemporalPose(json, "cameraFromBoardPosition", "cameraFromBoardRotation", pose);
            json.EndObject();
            WriteMotionJson(json.ToString(), now);
            _lastMotionVisualTimestampNs = sourceTimestampNs;
        }
        catch (Exception exception) { LogWriteFailure(exception); }
    }

    public void RecordUserMotionMarker(string marker)
    {
        if (!MotionDiagnosticsReady || (marker != "MOVE_START" && marker != "STOP")) return;
        try
        {
            var now = DjiBoardVisionBridge.GetMonotonicTimestampNs();
            WriteMotionJson(BuildMotionEventJson("UserMotionMarker", _telemetrySessionId, now, marker,
                "MANUAL_CUE_NOT_MEASURED_PHYSICAL_ONSET"), now, true);
        }
        catch (Exception exception) { LogWriteFailure(exception); }
    }

    private static bool IsMotionPropagation(DjiRenderPoseSource source) =>
        source == DjiRenderPoseSource.TelemetryPropagated;

    private void WriteMotionJson(string line, long timestampNs, bool flush = false)
    {
        _visualLatencyWriter.WriteLine(line);
        if (!flush && timestampNs < _nextMotionFlushNs) return;
        _visualLatencyWriter.Flush();
        _nextMotionFlushNs = timestampNs + 250_000_000L;
    }

    private static JsonLine MotionEventHeader(string type, string sessionId, long timestampNs)
    {
        var json = new JsonLine();
        json.BeginObject();
        json.Property("recordType").String(type);
        json.Property("telemetrySessionId").String(sessionId ?? "UNAVAILABLE");
        json.Property("clockDomain").String("ANDROID_MONOTONIC_elapsedRealtimeNanos");
        json.Property("monotonicTime").Number(timestampNs);
        json.Property("timestampUnit").String("ns");
        return json;
    }

    public static string BuildMotionEventJson(string type, string sessionId, long timestampNs, string value, string context)
    {
        var json = MotionEventHeader(type, sessionId, timestampNs);
        json.Property("value").String(value ?? string.Empty);
        json.Property("context").String(context ?? string.Empty);
        json.EndObject();
        return json.ToString();
    }

    public static string BuildHorizontalAdmissionTraceJson(string sessionId, HorizontalAdmissionTrace trace,
        string phase = "Evaluated", long applicationTimestampNs = 0L, Pose appliedPose = default,
        long admissionEvaluationTimestampNs = 0L)
    {
        var raw = trace.current.rawGnssCandidate;
        var estimate = trace.current.estimate;
        var json = MotionEventHeader("HorizontalAdmissionTrace", sessionId,
            applicationTimestampNs > 0L ? applicationTimestampNs : trace.monotonicTime);
        json.Property("evaluationTimestampNs").Number(trace.monotonicTime);
        json.Property("phase").String(phase);
        json.Property("nedReference").String("DISPLACEMENT_FROM_VISUAL_BASELINE");
        json.Property("visualBaselineTimestampNs").Number(trace.baselineTimestampNs);
        json.Property("gnssMeasurementTime").NullableNumber(raw.gnssTargetTimestampNs > 0 ? raw.gnssTargetTimestampNs : (long?)null);
        json.Property("measurementTimestampMeaning").String("ANDROID_CALLBACK_ARRIVAL_NOT_FC_SAMPLE_TIME");
        json.Property("gnssAgeMs").NullableNumber(double.IsFinite(raw.gnssTargetAgeMilliseconds) ? raw.gnssTargetAgeMilliseconds : (double?)null);
        json.Property("gnssFresh").Boolean(raw.valid);
        json.Property("horizontalQualityTrusted").Boolean(trace.quality.trusted);
        json.Property("qualityReason").String(trace.quality.admissionReason ?? "UNAVAILABLE");
        WriteMotionVector(json, "rawHorizontalNedPosition", trace.Candidate, raw.valid);
        WriteMotionVector(json, "displacementFromAcceptedTarget", trace.DisplacementFromAcceptedTarget, raw.valid);
        json.Property("candidateDisplacementMagnitude").NullableNumber(raw.valid ? trace.DisplacementFromAcceptedTarget.magnitude : (float?)null);
        json.Property("directionConsistencyCountBefore").Number(trace.previous.temporalConsistencyCount);
        json.Property("directionConsistencyCount").Number(estimate.targetAccepted && trace.previous.acceptedGnssTargetTimestampNs > 0L
            ? HorizontalGnssSmoothingEstimator.ProvisionalTemporalConsistencySampleCount : estimate.temporalConsistencyCount);
        json.Property("directionConsistencyCountAfter").Number(estimate.temporalConsistencyCount);
        json.Property("admitted").Boolean(estimate.targetAccepted);
        json.Property("rejectReason").String(estimate.targetRejectedReason ?? string.Empty);
        json.Property("smootherState").String(estimate.state.ToString());
        WriteMotionVector(json, "previousAcceptedTarget", trace.previous.acceptedGnssTarget, trace.previous.hasEstimate);
        json.Property("previousAcceptedTargetTimestampNs").Number(trace.previous.acceptedGnssTargetTimestampNs);
        WriteMotionVector(json, "candidateTarget", trace.Candidate, raw.valid);
        WriteMotionVector(json, "acceptedTarget", estimate.acceptedGnssTarget, estimate.hasEstimate);
        json.Property("acceptedTargetTimestampNs").Number(estimate.acceptedGnssTargetTimestampNs);
        WriteMotionVector(json, "smootherTarget", estimate.acceptedGnssTarget, estimate.hasEstimate);
        WriteMotionVector(json, "smootherOutput", estimate.smoothedNavigationDelta, estimate.hasEstimate);
        WriteMotionVector(json, "desiredCorrection", estimate.desiredCorrection, estimate.hasEstimate);
        WriteMotionVector(json, "correctionLimiterOutput", estimate.appliedCorrection, estimate.hasEstimate);
        json.Property("correctionSpeedMps").Number(estimate.correctionSpeedMetersPerSecond);
        json.Property("correctionLimited").Boolean(estimate.appliedCorrection.sqrMagnitude < estimate.desiredCorrection.sqrMagnitude);
        json.Property("attitudeTimestampNs").Number(trace.attitudeTimestampNs);
        json.Property("attitudeAgeMs").NullableNumber(double.IsFinite(trace.attitudeAgeMs) ? trace.attitudeAgeMs : (double?)null);
        json.Property("altitudeSourceTimestampNs").Number(trace.vertical.altitudeSourceTimestampNs);
        json.Property("altitudeAgeMs").NullableNumber(double.IsFinite(trace.vertical.altitudeAgeMilliseconds) ? trace.vertical.altitudeAgeMilliseconds : (double?)null);
        json.Property("altitudeFresh").Boolean(trace.vertical.sourceFresh);
        json.Property("smoothedNavigationDownM").Number(trace.vertical.smoothedDeltaNavigationDown);
        if (applicationTimestampNs > 0L)
        {
            json.Property("applicationTimestampNs").Number(applicationTimestampNs);
            json.Property("admissionEvaluationTimestampNs").Number(admissionEvaluationTimestampNs);
            WriteTemporalPose(json, "appliedCameraFromBoardPosition", "appliedCameraFromBoardRotation", appliedPose);
        }
        json.EndObject();
        return json.ToString();
    }

    private static void WriteMotionVector(JsonLine json, string name, Vector2 vector, bool available)
    {
        json.Property(name).BeginObject();
        json.Property("northM").NullableNumber(available ? vector.x : (float?)null);
        json.Property("eastM").NullableNumber(available ? vector.y : (float?)null);
        json.EndObject();
    }
}
