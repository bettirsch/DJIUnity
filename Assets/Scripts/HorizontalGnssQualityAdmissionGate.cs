/// <summary>
/// Admits horizontal GNSS targets only after the existing DJI satellite-count
/// telemetry has been consistently strong. Thresholds are provisional values
/// derived from the 20261014 physical session, not universal GNSS limits.
/// </summary>
public sealed class HorizontalGnssQualityAdmissionGate
{
    public const int ProvisionalEnterSatelliteCount = 23;
    public const int ProvisionalExitSatelliteCount = 21;
    public const int DefaultConfirmationSampleCount = 3;

    private long _lastProcessedCallbackTimestampNs;
    private bool _trusted;
    private int _confirmationCount;
    private HorizontalGnssQualityAdmissionSnapshot _snapshot;

    public HorizontalGnssQualityAdmissionGate()
    {
        Reset();
    }

    public HorizontalGnssQualityAdmissionSnapshot Snapshot => _snapshot;

    public void Reset()
    {
        _lastProcessedCallbackTimestampNs = 0L;
        _trusted = false;
        _confirmationCount = 0;
        _snapshot = new HorizontalGnssQualityAdmissionSnapshot(
            hasObservedGnssCallback: false,
            satelliteCount: null,
            trusted: false,
            transition: HorizontalGnssQualityTransition.None,
            confirmationCount: 0,
            admissionReason: "AWAITING_GNSS_QUALITY",
            sourceTimestampNs: 0L);
    }

    /// <summary>
    /// Processes one unique AircraftLocation3D callback timestamp. Repeated
    /// Unity polling of the same callback intentionally cannot advance trust.
    /// </summary>
    public HorizontalGnssQualityAdmissionSnapshot Update(long callbackTimestampNs, int? satelliteCount)
    {
        if (callbackTimestampNs <= 0L || callbackTimestampNs <= _lastProcessedCallbackTimestampNs)
            return _snapshot;

        _lastProcessedCallbackTimestampNs = callbackTimestampNs;
        var transition = HorizontalGnssQualityTransition.None;
        var completedTransition = false;
        string reason;
        if (!_trusted)
        {
            if (satelliteCount.HasValue && satelliteCount.Value >= ProvisionalEnterSatelliteCount)
            {
                _confirmationCount++;
                if (_confirmationCount >= DefaultConfirmationSampleCount)
                {
                    _trusted = true;
                    transition = HorizontalGnssQualityTransition.EnteredTrusted;
                    completedTransition = true;
                    reason = "SATELLITE_ENTER_CONFIRMED";
                }
                else
                {
                    reason = "SATELLITE_ENTER_CONFIRMING";
                }
            }
            else
            {
                _confirmationCount = 0;
                reason = satelliteCount.HasValue
                    ? "SATELLITE_COUNT_BELOW_ENTER_THRESHOLD"
                    : "SATELLITE_COUNT_UNAVAILABLE";
            }
        }
        else if (satelliteCount.HasValue && satelliteCount.Value > ProvisionalExitSatelliteCount)
        {
            _confirmationCount = 0;
            reason = satelliteCount.Value < ProvisionalEnterSatelliteCount
                ? "SATELLITE_HYSTERESIS_HOLD"
                : "SATELLITE_TRUSTED";
        }
        else
        {
            _confirmationCount++;
            if (_confirmationCount >= DefaultConfirmationSampleCount)
            {
                _trusted = false;
                transition = HorizontalGnssQualityTransition.ExitedTrusted;
                completedTransition = true;
                reason = "SATELLITE_EXIT_CONFIRMED";
            }
            else
            {
                reason = satelliteCount.HasValue
                    ? "SATELLITE_EXIT_CONFIRMING"
                    : "SATELLITE_COUNT_UNAVAILABLE";
            }
        }

        _snapshot = new HorizontalGnssQualityAdmissionSnapshot(
            hasObservedGnssCallback: true,
            satelliteCount: satelliteCount,
            trusted: _trusted,
            transition: transition,
            confirmationCount: _confirmationCount,
            admissionReason: reason,
            sourceTimestampNs: callbackTimestampNs);
        if (completedTransition)
            _confirmationCount = 0;
        return _snapshot;
    }
}

public enum HorizontalGnssQualityTransition
{
    None,
    EnteredTrusted,
    ExitedTrusted
}

public readonly struct HorizontalGnssQualityAdmissionSnapshot
{
    public readonly bool hasObservedGnssCallback;
    public readonly int? satelliteCount;
    public readonly bool trusted;
    public readonly HorizontalGnssQualityTransition transition;
    public readonly int confirmationCount;
    public readonly string admissionReason;
    public readonly long sourceTimestampNs;

    public HorizontalGnssQualityAdmissionSnapshot(
        bool hasObservedGnssCallback,
        int? satelliteCount,
        bool trusted,
        HorizontalGnssQualityTransition transition,
        int confirmationCount,
        string admissionReason,
        long sourceTimestampNs)
    {
        this.hasObservedGnssCallback = hasObservedGnssCallback;
        this.satelliteCount = satelliteCount;
        this.trusted = trusted;
        this.transition = transition;
        this.confirmationCount = confirmationCount;
        this.admissionReason = admissionReason ?? string.Empty;
        this.sourceTimestampNs = sourceTimestampNs;
    }
}
