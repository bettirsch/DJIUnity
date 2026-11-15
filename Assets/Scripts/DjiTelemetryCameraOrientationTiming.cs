using System;
using UnityEngine;

public sealed partial class DjiTelemetryValidationLogger
{
    private long _nextOrientationTimingNs;
    private DjiRenderPoseSource _lastOrientationTimingSource;

    private void RecordCameraOrientationTiming(long appliedNs, DjiRenderPoseSource source, Pose appliedPose)
    {
        if (!MotionDiagnosticsReady || appliedNs <= 0L ||
            (appliedNs < _nextOrientationTimingNs && source == _lastOrientationTimingSource)) return;
        try
        {
            var serializationStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var selectedTimestamp = IsMotionPropagation(source) ? _latestMotionTrace.attitudeTimestampNs : 0L;
            DjiCameraPoseProvider.TelemetryOrientationSample? selected = null;
            var history = _telemetryProvider?.TelemetryOrientationHistory;
            if (history != null)
                foreach (var sample in history)
                {
                    if (sample.timestampNs > appliedNs) break;
                    if (selectedTimestamp == 0L || sample.timestampNs == selectedTimestamp) selected = sample;
                }
            _visualLatencyWriter.WriteLine(BuildCameraOrientationTimingJson(_telemetrySessionId, appliedNs,
                source, appliedPose, selected, IsMotionPropagation(source) && selectedTimestamp > 0L));
            if (_visualLatencyWriter is BoundedDiagnosticJsonlWriter buffered)
                buffered.ObserveSerialization(System.Diagnostics.Stopwatch.GetTimestamp() - serializationStart);
            _nextOrientationTimingNs = appliedNs + 200_000_000L;
            _lastOrientationTimingSource = source;
        }
        catch (Exception) { if (_visualLatencyWriter is BoundedDiagnosticJsonlWriter buffered) buffered.ReportCaptureError(); }
    }

    public static string BuildCameraOrientationTimingJson(string session, long appliedNs,
        DjiRenderPoseSource source, Pose appliedPose,
        DjiCameraPoseProvider.TelemetryOrientationSample? selected, bool selectedForPropagation)
    {
        if (selected.HasValue && (!selected.Value.hasOrientation || selected.Value.timestampNs > appliedNs)) selected = null;
        var j = MotionEventHeader("CameraOrientationTiming", session, appliedNs);
        j.Property("renderAuthority").String(source.ToString());
        j.Property("renderApplicationTimestampNs").Number(appliedNs);
        j.Property("telemetryTimestampSemantics").String("ANDROID_CALLBACK_ARRIVAL_NOT_FC_CAPTURE");
        j.Property("cameraOrientationCalculationTimestampNs").NullableNumber(selected.HasValue && selected.Value.calculationTimestampNs > 0L ? selected.Value.calculationTimestampNs : (long?)null);
        j.Property("selectedTelemetryUsedForPropagation").Boolean(selectedForPropagation && selected.HasValue);
        j.Property("selectionMethod").String(selectedForPropagation ? "EXACT_PROPAGATION_SAMPLE_HOLD_NO_EXTRAPOLATION" : "DIAGNOSTIC_LATEST_CAUSAL_NOT_VISUAL_AUTHORITY");
        j.Property("componentAgeLimitPolicy").String("NO_NEW_COMPONENT_AGE_LIMIT_DIAGNOSTIC_ONLY");
        WriteOrientationEndpoint(j, "selectedTelemetry", selected);
        var p = selected.GetValueOrDefault().provenance;
        j.Property("aggregateAgeMs").NullableNumber(selected.HasValue && appliedNs >= selected.Value.timestampNs ? (appliedNs - selected.Value.timestampNs) / 1e6 : (double?)null);
        j.Property("aircraftComponentAgeMs").NullableNumber(p.available && p.aircraftArrivalNs > 0L && p.aircraftArrivalNs <= appliedNs ? (appliedNs - p.aircraftArrivalNs) / 1e6 : (double?)null);
        j.Property("gimbalComponentAgeMs").NullableNumber(p.available && p.gimbalArrivalNs > 0L && p.gimbalArrivalNs <= appliedNs ? (appliedNs - p.gimbalArrivalNs) / 1e6 : (double?)null);
        WriteTemporalPose(j, "appliedCameraFromBoardPosition", "appliedCameraFromBoardRotation", appliedPose);
        return j.EndObject().ToString();
    }
}
