using System;
using System.IO;
using UnityEngine;

public sealed partial class DjiTelemetryValidationLogger
{
#if DJI_RELATIVE_POSITION_EXPERIMENT && UNITY_ANDROID && !UNITY_EDITOR
    private bool _relativePositionExperimentStarted;
    private long _nextRelativePositionDrainNs;
#endif

    private void StartRelativePositionExperiment()
    {
#if DJI_RELATIVE_POSITION_EXPERIMENT && UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using var bridge = new AndroidJavaClass("com.sok9hu.djibridge.DJIPoseBridge");
            _relativePositionExperimentStarted = bridge.CallStatic<bool>("startRelativePositionExperiment", _telemetrySessionId);
            _nextRelativePositionDrainNs = 0;
            Debug.Log($"DJI_RELATIVE_POSITION_EXPERIMENT_STARTED enabled={_relativePositionExperimentStarted} sessionId={_telemetrySessionId} path={_visualLatencyPath} localizationInput=false");
        }
        catch (Exception e) { Debug.LogWarning("DJI_RELATIVE_POSITION_EXPERIMENT_START_FAILED " + e); }
#endif
    }

    private void DrainRelativePositionExperiment(long nowTimestampNs)
    {
#if DJI_RELATIVE_POSITION_EXPERIMENT && UNITY_ANDROID && !UNITY_EDITOR
        if (!_relativePositionExperimentStarted || nowTimestampNs < _nextRelativePositionDrainNs) return;
        _nextRelativePositionDrainNs = nowTimestampNs + 100_000_000L;
        try
        {
            using var bridge = new AndroidJavaClass("com.sok9hu.djibridge.DJIPoseBridge");
            WriteRelativePositionExperimentBatch(_visualLatencyWriter, _telemetrySessionId,
                bridge.CallStatic<string>("drainRelativePositionExperiment", _telemetrySessionId));
            _visualLatencyWriter.Flush();
        }
        catch (Exception e) { LogWriteFailure(e); }
#endif
    }

    private void StopRelativePositionExperiment()
    {
#if DJI_RELATIVE_POSITION_EXPERIMENT && UNITY_ANDROID && !UNITY_EDITOR
        if (!_relativePositionExperimentStarted) return;
        try
        {
            using var bridge = new AndroidJavaClass("com.sok9hu.djibridge.DJIPoseBridge");
            WriteRelativePositionExperimentBatch(_visualLatencyWriter, _telemetrySessionId,
                bridge.CallStatic<string>("stopRelativePositionExperiment", _telemetrySessionId));
            _visualLatencyWriter?.Flush();
        }
        catch (Exception e) { LogWriteFailure(e); }
        finally { _relativePositionExperimentStarted = false; }
#endif
    }

    [Serializable]
    private sealed class RelativePositionDiagnosticHeader
    {
        public string recordType;
        public string telemetrySessionId;
        public bool appliedToLocalization;
    }

    // Validate the cross-language envelope without copying any XYZ into world state.
    public static int WriteRelativePositionExperimentBatch(TextWriter writer, string sessionId, string batch)
    {
        if (writer == null || string.IsNullOrEmpty(batch) || string.IsNullOrEmpty(sessionId)) return 0;
        int count = 0;
        using var reader = new StringReader(batch);
        while (reader.ReadLine() is string line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var header = JsonUtility.FromJson<RelativePositionDiagnosticHeader>(line);
            if (header == null || header.recordType != "RelativePositionExperiment" ||
                header.telemetrySessionId != sessionId || header.appliedToLocalization)
                throw new InvalidDataException("Relative-position diagnostic envelope mismatch.");
            writer.WriteLine(line);
            count++;
        }
        return count;
    }
}
