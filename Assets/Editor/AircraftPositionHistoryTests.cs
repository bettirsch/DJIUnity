using NUnit.Framework;
using UnityEngine;

public sealed class AircraftPositionHistoryTests
{
    [Test]
    public void PositionHistorySkipsReplayedSamplesAndKeepsNewerCallbacks()
    {
        var history = new TelemetryAircraftPositionHistory(4);
        var firstBatch = new[]
        {
            new TelemetryAircraftPositionSample(100L, 47.0, 19.0, 100.0),
            new TelemetryAircraftPositionSample(200L, 47.0, 19.0, 100.0)
        };
        Assert.That(history.SyncBridgeSamples(firstBatch), Is.EqualTo(2));

        var replayPlusNew = new[]
        {
            firstBatch[0],
            firstBatch[1],
            new TelemetryAircraftPositionSample(300L, 47.0, 19.000001, 100.0)
        };
        Assert.That(history.SyncBridgeSamples(replayPlusNew), Is.EqualTo(1));
        Assert.That(history.Count, Is.EqualTo(3));
        Assert.That(history.NewestTimestampNs, Is.EqualTo(300L));
    }

    [Test]
    public void Wgs84HorizontalDeltaPreservesNorthAndEastDirections()
    {
        var north = DjiGeodesy.HorizontalNedDelta(0.0, 0.0, 0.00001, 0.0);
        var east = DjiGeodesy.HorizontalNedDelta(0.0, 0.0, 0.0, 0.00001);

        Assert.That(north.x, Is.GreaterThan(1.0f));
        Assert.That(Mathf.Abs(north.y), Is.LessThan(0.001f));
        Assert.That(east.y, Is.GreaterThan(1.0f));
        Assert.That(Mathf.Abs(east.x), Is.LessThan(0.001f));
    }

    [Test]
    public void PositionFreshnessRequiresAnEarlierRecentSample()
    {
        Assert.That(
            DjiAircraftPositionFreshness.IsFreshAtOrBefore(1_000_000_000L, 1_300_000_000L, 300f, out var freshAgeMs),
            Is.True);
        Assert.That(freshAgeMs, Is.EqualTo(300d).Within(0.001d));

        Assert.That(
            DjiAircraftPositionFreshness.IsFreshAtOrBefore(1_000_000_000L, 1_301_000_000L, 300f, out _),
            Is.False);
        Assert.That(
            DjiAircraftPositionFreshness.IsFreshAtOrBefore(1_001_000_000L, 1_000_000_000L, 300f, out _),
            Is.False);
    }
}
