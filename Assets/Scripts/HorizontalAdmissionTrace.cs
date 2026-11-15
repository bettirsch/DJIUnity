using UnityEngine;

/// <summary>Read-only copies of one production evaluation; owns no estimator.</summary>
public readonly struct HorizontalAdmissionTrace
{
    public readonly long monotonicTime;
    public readonly long baselineTimestampNs;
    public readonly HorizontalGnssSmoothingEstimate previous;
    public readonly HorizontalGnssSmoothingShadowSnapshot current;
    public readonly HorizontalGnssQualityAdmissionSnapshot quality;
    public readonly long attitudeTimestampNs;
    public readonly double attitudeAgeMs;
    public readonly VerticalAltitudeProductionSnapshot vertical;

    public HorizontalAdmissionTrace(long monotonicTime, HorizontalGnssSmoothingEstimate previous,
        HorizontalGnssSmoothingShadowSnapshot current, HorizontalGnssQualityAdmissionSnapshot quality,
        long attitudeTimestampNs, double attitudeAgeMs, VerticalAltitudeProductionSnapshot vertical,
        long baselineTimestampNs = 0L)
    {
        this.monotonicTime = monotonicTime;
        this.previous = previous;
        this.current = current;
        this.quality = quality;
        this.attitudeTimestampNs = attitudeTimestampNs;
        this.attitudeAgeMs = attitudeAgeMs;
        this.vertical = vertical;
        this.baselineTimestampNs = baselineTimestampNs > 0L ? baselineTimestampNs : current.visualBaselineTimestampNs;
    }

    public Vector2 Candidate => new(current.rawGnssCandidate.navigationDelta.x, current.rawGnssCandidate.navigationDelta.y);
    public Vector2 DisplacementFromAcceptedTarget => Candidate - previous.acceptedGnssTarget;
}

/// <summary>Diagnostic deduplication only. Never feeds state back into localization.</summary>
public sealed class HorizontalAdmissionTraceGate
{
    private long _lastEvaluation;
    private long _baseline;
    private long _source;
    private bool _fresh;
    private bool _trusted;
    private HorizontalGnssSmoothingState _state;

    public bool ShouldWrite(HorizontalAdmissionTrace trace, bool enabled = true)
    {
        if (!enabled || trace.monotonicTime <= _lastEvaluation || trace.baselineTimestampNs <= 0L)
            return false;
        var source = trace.current.rawGnssCandidate.gnssTargetTimestampNs;
        var fresh = trace.current.rawGnssCandidate.valid;
        var changed = trace.baselineTimestampNs != _baseline || source > _source ||
                      fresh != _fresh || trace.quality.trusted != _trusted || trace.current.estimate.state != _state;
        _lastEvaluation = trace.monotonicTime;
        _baseline = trace.baselineTimestampNs;
        _source = source;
        _fresh = fresh;
        _trusted = trace.quality.trusted;
        _state = trace.current.estimate.state;
        return changed;
    }
}
