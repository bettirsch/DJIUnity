using UnityEngine;

public static class DjiAprilTagNoMarkersQualityLossExperiment
{
    public static bool Enabled
    {
        get
        {
#if DJI_APRILTAG_NO_MARKERS_QUALITY_LOSS_EXPERIMENT
            return true;
#else
            return false;
#endif
        }
    }
}

public enum NoMarkersQualityLossDecision
{
    Applied,
    InvalidSourceTimestamp,
    NotNewerThanAcceptedVisual,
    OutOfOrderSourceTimestamp,
    OlderListenerGeneration,
    NonIncreasingFrameSequence
}

/// <summary>Ordering fence only for the experimental no-markers quality-loss action.</summary>
public struct NoMarkersFrameOrder
{
    public long latestTimestampNs, listenerGeneration;
    public int frameSequence;

    public NoMarkersQualityLossDecision Evaluate(long sourceNs, long generation, int sequence, long acceptedVisualNs)
    {
        if (sourceNs <= 0L || generation < 0L || sequence < 0 || (generation > 0L && sequence == 0))
            return NoMarkersQualityLossDecision.InvalidSourceTimestamp;
        if (sourceNs <= acceptedVisualNs)
            return NoMarkersQualityLossDecision.NotNewerThanAcceptedVisual;
        if (generation > 0L && listenerGeneration > 0L && generation < listenerGeneration)
            return NoMarkersQualityLossDecision.OlderListenerGeneration;
        if (sourceNs <= latestTimestampNs)
            return NoMarkersQualityLossDecision.OutOfOrderSourceTimestamp;
        // Old AARs do not expose a generation. Their monotonic timestamp is
        // still usable, but a sequence reset must not invent a new epoch.
        if (generation > 0L && generation == listenerGeneration && sequence <= frameSequence)
            return NoMarkersQualityLossDecision.NonIncreasingFrameSequence;
        return NoMarkersQualityLossDecision.Applied;
    }

    public void Observe(long sourceNs, long generation, int sequence)
    {
        if (sourceNs <= latestTimestampNs || generation < 0L || sequence < 0 ||
            (generation > 0L && listenerGeneration > generation)) return;
        latestTimestampNs = sourceNs;
        if (generation > 0L)
        {
            frameSequence = generation == listenerGeneration ? System.Math.Max(frameSequence, sequence) : sequence;
            listenerGeneration = generation;
        }
    }
}

public readonly struct NoMarkersQualityLossDiagnostic
{
    public readonly long arrivalNs, sourceNs, generation, acceptedVisualNs;
    public readonly int nativeStatus, markerCount, sequence;
    public readonly NoMarkersQualityLossDecision decision;
    public readonly DjiBoardVisionProvider.VisualPoseQuality previousQuality, currentQuality;
    public readonly DjiRenderPoseSource renderSource;

    public NoMarkersQualityLossDiagnostic(long arrivalNs, long sourceNs, long generation, int sequence,
        int nativeStatus, int markerCount, long acceptedVisualNs, NoMarkersQualityLossDecision decision,
        DjiBoardVisionProvider.VisualPoseQuality previousQuality,
        DjiBoardVisionProvider.VisualPoseQuality currentQuality, DjiRenderPoseSource renderSource)
    {
        this.arrivalNs = arrivalNs; this.sourceNs = sourceNs; this.generation = generation;
        this.sequence = sequence; this.nativeStatus = nativeStatus; this.markerCount = markerCount;
        this.acceptedVisualNs = acceptedVisualNs; this.decision = decision;
        this.previousQuality = previousQuality; this.currentQuality = currentQuality; this.renderSource = renderSource;
    }
}

public sealed partial class DjiBoardVisionProvider
{
    private NoMarkersFrameOrder _noMarkersFrameOrder;
    private bool _hasNoMarkersDiagnostic;
    private NoMarkersQualityLossDecision _lastNoMarkersDecision;
    private long _lastNoMarkersDiagnosticGeneration, _nextNoMarkersDiagnosticNs;

    private bool TryHandleNoMarkersQualityLoss(NativeBoardResult result, long arrivalNs, bool enabled)
    {
        if (!enabled) return false;
        var sourceNs = GetVisionTimestamp(result);
        var isNoMarkers = result.markerCount == 0 && (result.status == -8 || result.status >= 0);
        if (!isNoMarkers)
        {
            if (sourceNs <= arrivalNs)
                _noMarkersFrameOrder.Observe(sourceNs, result.listenerGeneration, result.frameSequence);
            return false;
        }

        var previousQuality = CurrentVisualPoseQuality;
        var decision = arrivalNs <= 0L || sourceNs > arrivalNs
            ? NoMarkersQualityLossDecision.InvalidSourceTimestamp
            : _noMarkersFrameOrder.Evaluate(sourceNs, result.listenerGeneration,
                result.frameSequence, _currentHighConfidenceVisualTimestampNs);
        if (decision == NoMarkersQualityLossDecision.Applied)
        {
            _noMarkersFrameOrder.Observe(sourceNs, result.listenerGeneration, result.frameSequence);
            // Reuse the existing zero-marker transition. Keep the accepted
            // visual pose and exact propagation baseline; only quality is lost.
            UpdateVisualPoseQuality(VisualPoseMetrics.Unavailable(sourceNs), "NO_MARKERS_DETECTED");
            State = LocalizationState.WaitingForReference;
        }

        if (DetailedLogging && (!_hasNoMarkersDiagnostic || decision != _lastNoMarkersDecision ||
            result.listenerGeneration != _lastNoMarkersDiagnosticGeneration ||
            previousQuality != CurrentVisualPoseQuality || arrivalNs >= _nextNoMarkersDiagnosticNs))
        {
            _telemetryValidationLogger?.RecordNoMarkersQualityLoss(new NoMarkersQualityLossDiagnostic(
                arrivalNs, sourceNs, result.listenerGeneration, result.frameSequence, result.status, result.markerCount,
                _currentHighConfidenceVisualTimestampNs, decision, previousQuality, CurrentVisualPoseQuality, CurrentRenderPoseSource));
            _hasNoMarkersDiagnostic = true;
            _lastNoMarkersDecision = decision;
            _lastNoMarkersDiagnosticGeneration = result.listenerGeneration;
            _nextNoMarkersDiagnosticNs = arrivalNs + 1_000_000_000L;
        }
        return true;
    }
}

public sealed partial class DjiTelemetryValidationLogger
{
    public void RecordNoMarkersQualityLoss(NoMarkersQualityLossDiagnostic record)
    {
        if (!MotionDiagnosticsReady) return;
        try { WriteMotionJson(BuildNoMarkersQualityLossJson(_telemetrySessionId, record), record.arrivalNs); }
        catch (System.Exception)
        {
            if (_visualLatencyWriter is BoundedDiagnosticJsonlWriter buffered) buffered.ReportCaptureError();
        }
    }

    public static string BuildNoMarkersQualityLossJson(string sessionId, NoMarkersQualityLossDiagnostic r)
    {
        var j = MotionEventHeader("NoMarkersQualityLoss", sessionId, r.arrivalNs);
        j.Property("experimentEnabled").Boolean(true);
        j.Property("nativeStatus").Number(r.nativeStatus);
        j.Property("detectedTagCount").Number(r.markerCount);
        j.Property("sourceTimestampNs").Number(r.sourceNs);
        j.Property("listenerGeneration").NullableNumber(r.generation > 0L ? r.generation : (long?)null);
        j.Property("generationAvailable").Boolean(r.generation > 0L);
        j.Property("frameSequence").Number(r.sequence);
        j.Property("decision").String(r.decision.ToString());
        j.Property("previousVisualQuality").String(r.previousQuality.ToString());
        j.Property("visualQuality").String(r.currentQuality.ToString());
        j.Property("lastAcceptedVisualTimestampNs").Number(r.acceptedVisualNs);
        j.Property("renderAuthorityAtDecision").String(r.renderSource.ToString());
        return j.EndObject().ToString();
    }
}
