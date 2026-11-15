using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// An owned KeyAircraftLocation3D callback sample. Its altitude is retained
/// for audit only; horizontal diagnostics never use it as a vertical datum.
/// </summary>
public readonly struct TelemetryAircraftPositionSample
{
    public readonly long timestampNs;
    public readonly double latitudeDegrees;
    public readonly double longitudeDegrees;
    public readonly double altitude;

    public TelemetryAircraftPositionSample(long timestampNs, double latitudeDegrees, double longitudeDegrees, double altitude)
    {
        this.timestampNs = timestampNs;
        this.latitudeDegrees = latitudeDegrees;
        this.longitudeDegrees = longitudeDegrees;
        this.altitude = altitude;
    }
}

/// <summary>Bounded, idempotent callback history for absolute aircraft position diagnostics.</summary>
public sealed class TelemetryAircraftPositionHistory
{
    private readonly int _capacity;
    private readonly List<TelemetryAircraftPositionSample> _samples = new();

    public TelemetryAircraftPositionHistory(int capacity) => _capacity = Math.Max(1, capacity);

    public IReadOnlyList<TelemetryAircraftPositionSample> Samples => _samples;
    public int Count => _samples.Count;
    public long NewestTimestampNs => _samples.Count == 0 ? 0L : _samples[_samples.Count - 1].timestampNs;

    /// <summary>Returns the newest sample that cannot observe the future.</summary>
    public bool TryGetAtOrBefore(long targetTimestampNs, out TelemetryAircraftPositionSample sample)
    {
        sample = default;
        if (targetTimestampNs <= 0L)
            return false;

        for (var index = _samples.Count - 1; index >= 0; index--)
        {
            if (_samples[index].timestampNs > targetTimestampNs)
                continue;

            sample = _samples[index];
            return true;
        }

        return false;
    }

    public int SyncBridgeSamples(IEnumerable<TelemetryAircraftPositionSample> bridgeSamples)
    {
        var appended = 0;
        var knownThrough = NewestTimestampNs;
        foreach (var sample in bridgeSamples)
        {
            // The bridge returns its complete bounded history on every poll.
            if (sample.timestampNs <= knownThrough)
                continue;
            if (!IsValid(sample) || (NewestTimestampNs > 0L && sample.timestampNs <= NewestTimestampNs))
                continue;

            _samples.Add(sample);
            appended++;
            if (_samples.Count > _capacity)
                _samples.RemoveAt(0);
        }
        return appended;
    }

    private static bool IsValid(TelemetryAircraftPositionSample sample) =>
        sample.timestampNs > 0L &&
        double.IsFinite(sample.latitudeDegrees) && sample.latitudeDegrees >= -90d && sample.latitudeDegrees <= 90d &&
        double.IsFinite(sample.longitudeDegrees) && sample.longitudeDegrees >= -180d && sample.longitudeDegrees <= 180d &&
        double.IsFinite(sample.altitude);
}

/// <summary>
/// Timestamp policy shared by horizontal GNSS diagnostics. A position sample
/// is usable only when it precedes the target and is still fresh at that time.
/// </summary>
public static class DjiAircraftPositionFreshness
{
    public static bool IsFreshAtOrBefore(
        long sampleTimestampNs,
        long targetTimestampNs,
        float maximumAgeMilliseconds,
        out double ageMilliseconds)
    {
        ageMilliseconds = double.PositiveInfinity;
        if (sampleTimestampNs <= 0L || targetTimestampNs < sampleTimestampNs || maximumAgeMilliseconds < 0f)
            return false;

        ageMilliseconds = (targetTimestampNs - sampleTimestampNs) / 1_000_000.0;
        return ageMilliseconds <= maximumAgeMilliseconds;
    }
}

/// <summary>WGS84 local tangent-plane helpers for horizontal diagnostics only.</summary>
public static class DjiGeodesy
{
    private const double SemiMajorAxisMeters = 6378137.0;
    private const double Flattening = 1.0 / 298.257223563;
    private const double EccentricitySquared = Flattening * (2.0 - Flattening);

    /// <summary>
    /// Returns horizontal N/E displacement using a WGS84 ECEF-to-local-NED
    /// transform. Both points use ellipsoid height zero, intentionally keeping
    /// unvalidated DJI altitude out of this diagnostic.
    /// </summary>
    public static Vector2 HorizontalNedDelta(
        double referenceLatitudeDegrees,
        double referenceLongitudeDegrees,
        double targetLatitudeDegrees,
        double targetLongitudeDegrees)
    {
        var referenceEcef = ToEcef(referenceLatitudeDegrees, referenceLongitudeDegrees);
        var targetEcef = ToEcef(targetLatitudeDegrees, targetLongitudeDegrees);
        var delta = targetEcef - referenceEcef;
        var latitude = referenceLatitudeDegrees * Math.PI / 180.0;
        var longitude = referenceLongitudeDegrees * Math.PI / 180.0;
        var north = -Math.Sin(latitude) * Math.Cos(longitude) * delta.x
                    - Math.Sin(latitude) * Math.Sin(longitude) * delta.y
                    + Math.Cos(latitude) * delta.z;
        var east = -Math.Sin(longitude) * delta.x + Math.Cos(longitude) * delta.y;
        return new Vector2((float)north, (float)east);
    }

    private static Double3 ToEcef(double latitudeDegrees, double longitudeDegrees)
    {
        var latitude = latitudeDegrees * Math.PI / 180.0;
        var longitude = longitudeDegrees * Math.PI / 180.0;
        var sinLatitude = Math.Sin(latitude);
        var radius = SemiMajorAxisMeters / Math.Sqrt(1.0 - EccentricitySquared * sinLatitude * sinLatitude);
        return new Double3(
            radius * Math.Cos(latitude) * Math.Cos(longitude),
            radius * Math.Cos(latitude) * Math.Sin(longitude),
            radius * (1.0 - EccentricitySquared) * sinLatitude);
    }

    private readonly struct Double3
    {
        public readonly double x;
        public readonly double y;
        public readonly double z;

        public Double3(double x, double y, double z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public static Double3 operator -(Double3 left, Double3 right) =>
            new(left.x - right.x, left.y - right.y, left.z - right.z);
    }
}
