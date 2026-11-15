using System;

/// <summary>Source identity for raw DJI horizontal-quality callback diagnostics.</summary>
public enum HorizontalQualitySampleKind
{
    GpsInterferenceState,
    FusionPositioningLevel
}

/// <summary>
/// Raw callback data retained only for telemetry JSONL correlation. These
/// records do not participate in horizontal admission or localization.
/// </summary>
public readonly struct HorizontalQualityCallbackRecord
{
    public readonly HorizontalQualitySampleKind source;
    public readonly long timestampNs;
    public readonly string rawValue;
    public readonly string status;
    public readonly string error;

    public HorizontalQualityCallbackRecord(
        HorizontalQualitySampleKind source,
        long timestampNs,
        string rawValue,
        string status,
        string error)
    {
        this.source = source;
        this.timestampNs = timestampNs;
        this.rawValue = rawValue;
        this.status = status;
        this.error = error;
    }
}

/// <summary>Suppresses replayed bridge history without suppressing new callbacks.</summary>
public sealed class HorizontalQualitySampleDeduplicator
{
    private readonly long[] _latestTimestampNs = new long[2];

    public bool TryAccept(HorizontalQualitySampleKind source, long timestampNs)
    {
        var index = (int)source;
        if (timestampNs <= 0L || timestampNs <= _latestTimestampNs[index])
            return false;

        _latestTimestampNs[index] = timestampNs;
        return true;
    }
}
