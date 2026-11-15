using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public sealed class DjiWorldLocalizationVerticalAltitudePropagationTests
{
    private const float Tolerance = 0.0001f;

    [Test]
    public void FreshPositiveAltitude_ProducesNegativeNavigationDownAndPreservesWorldBoard()
    {
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var samples = Samples(
            new TelemetryAircraftPositionSample(100_000_000L, 47d, 19d, 100d),
            new TelemetryAircraftPositionSample(500_000_000L, 47d, 19d, 101d));
        Assert.That(coordinator.TryInitializeFromVisualMeasurement(Pose.identity, 100_000_000L, true, out var initializeReason), Is.True, initializeReason);
        var board = coordinator.Snapshot.worldFromBoard;
        Assert.That(coordinator.TryBeginRotationPropagation(Pose.identity, 100_000_000L, Quaternion.identity, samples, 300f, out var beginReason), Is.True, beginReason);

        Assert.That(coordinator.TryUpdateRotationPropagation(500_000_000L, Quaternion.identity, Pose.identity, samples, 300f, out var updateReason), Is.True, updateReason);

        var vertical = coordinator.ProductionVerticalAltitude;
        Assert.That(vertical.baselineAvailable, Is.True);
        Assert.That(vertical.sourceFresh, Is.True);
        Assert.That(vertical.rawDeltaAltitudeUp, Is.EqualTo(1f));
        Assert.That(vertical.rawDeltaNavigationDown, Is.EqualTo(-1f));
        Assert.That(vertical.smoothedDeltaNavigationDown, Is.LessThan(0f));
        AssertPose(coordinator.Snapshot.worldFromBoard, board);
    }

    [Test]
    public void VerticalContribution_UsesWorldNavigationRotationInsteadOfUnityAxisAssumption()
    {
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var worldFromNavigation = Quaternion.Euler(17f, -34f, 23f);
        var visual = new Pose(Vector3.zero, Quaternion.Inverse(worldFromNavigation));
        var samples = Samples(
            new TelemetryAircraftPositionSample(100_000_000L, 47d, 19d, 100d),
            new TelemetryAircraftPositionSample(500_000_000L, 47d, 19d, 99d));
        Assert.That(coordinator.TryInitializeFromVisualMeasurement(visual, 100_000_000L, true, out var initializeReason), Is.True, initializeReason);
        Assert.That(coordinator.TryBeginRotationPropagation(visual, 100_000_000L, Quaternion.identity, samples, 300f, out var beginReason), Is.True, beginReason);

        Assert.That(coordinator.TryUpdateRotationPropagation(500_000_000L, Quaternion.identity, visual, samples, 300f, out var updateReason), Is.True, updateReason);

        var vertical = coordinator.ProductionVerticalAltitude;
        var expected = worldFromNavigation * new Vector3(0f, 0f, vertical.smoothedDeltaNavigationDown);
        Assert.That(Vector3.Distance(vertical.worldVerticalContribution, expected), Is.LessThan(Tolerance));
    }

    [Test]
    public void StaleAltitude_FreezesVerticalWhileRotationPropagationContinues()
    {
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var samples = Samples(new TelemetryAircraftPositionSample(100_000_000L, 47d, 19d, 100d));
        Assert.That(coordinator.TryInitializeFromVisualMeasurement(Pose.identity, 100_000_000L, true, out var initializeReason), Is.True, initializeReason);
        Assert.That(coordinator.TryBeginRotationPropagation(Pose.identity, 100_000_000L, Quaternion.identity, samples, 300f, out var beginReason), Is.True, beginReason);

        Assert.That(coordinator.TryUpdateRotationPropagation(500_000_000L, Quaternion.Euler(0f, 10f, 0f), Pose.identity, samples, 300f, out var updateReason), Is.True, updateReason);

        Assert.That(coordinator.Snapshot.phase, Is.EqualTo(WorldLocalizationPhase.Propagating));
        Assert.That(coordinator.ProductionVerticalAltitude.estimate.state, Is.EqualTo(VerticalAltitudeLocalizationState.DegradedStale));
        Assert.That(coordinator.ProductionVerticalAltitude.smoothedDeltaNavigationDown, Is.EqualTo(0f));
    }

    [Test]
    public void MissingBaselineAltitude_LeavesVerticalHeldForEntireOutage()
    {
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var futureOnly = Samples(new TelemetryAircraftPositionSample(101_000_000L, 47d, 19d, 100d));
        Assert.That(coordinator.TryInitializeFromVisualMeasurement(Pose.identity, 100_000_000L, true, out var initializeReason), Is.True, initializeReason);
        Assert.That(coordinator.TryBeginRotationPropagation(Pose.identity, 100_000_000L, Quaternion.identity, futureOnly, 300f, out var beginReason), Is.True, beginReason);

        Assert.That(coordinator.TryUpdateRotationPropagation(200_000_000L, Quaternion.identity, Pose.identity, futureOnly, 300f, out var updateReason), Is.True, updateReason);

        Assert.That(coordinator.ProductionVerticalAltitude.estimate.state, Is.EqualTo(VerticalAltitudeLocalizationState.BaselineUnavailable));
        Assert.That(coordinator.ProductionVerticalAltitude.hasContribution, Is.False);
    }

    [Test]
    public void VisualReacquisition_ResetsVerticalBaselineForNextOutage()
    {
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var samples = Samples(
            new TelemetryAircraftPositionSample(100_000_000L, 47d, 19d, 100d),
            new TelemetryAircraftPositionSample(200_000_000L, 47d, 19d, 101d));
        Assert.That(coordinator.TryInitializeFromVisualMeasurement(Pose.identity, 100_000_000L, true, out var initializeReason), Is.True, initializeReason);
        Assert.That(coordinator.TryBeginRotationPropagation(Pose.identity, 100_000_000L, Quaternion.identity, samples, 300f, out var beginReason), Is.True, beginReason);
        Assert.That(coordinator.TryUpdateRotationPropagation(200_000_000L, Quaternion.identity, Pose.identity, samples, 300f, out var updateReason), Is.True, updateReason);

        Assert.That(coordinator.TryUpdateFromVisualMeasurement(Pose.identity, 300_000_000L, true, samples, 300f, out var visualReason), Is.True, visualReason);

        Assert.That(coordinator.ProductionVerticalAltitude.estimate.state, Is.EqualTo(VerticalAltitudeLocalizationState.BaselineUnavailable));
    }

    [TestCase(47.00002d, 19.00002d, 101d)]
    [TestCase(47d, 19.00002d, 99d)]
    public void HorizontalAndVerticalContributions_ComposeInNavigationBeforeWorldMapping(
        double targetLatitude,
        double targetLongitude,
        double targetAltitude)
    {
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var samples = Samples(
            new TelemetryAircraftPositionSample(100_000_000L, 47d, 19d, 100d),
            new TelemetryAircraftPositionSample(200_000_000L, (47d + targetLatitude) / 2d, (19d + targetLongitude) / 2d, targetAltitude),
            new TelemetryAircraftPositionSample(300_000_000L, targetLatitude, targetLongitude, targetAltitude));
        Assert.That(coordinator.TryInitializeFromVisualMeasurement(Pose.identity, 100_000_000L, true, out var initializeReason), Is.True, initializeReason);
        Assert.That(coordinator.TryBeginRotationPropagation(Pose.identity, 100_000_000L, Quaternion.identity, samples, 300f, out var beginReason), Is.True, beginReason);
        Assert.That(coordinator.TryUpdateRotationPropagation(200_000_000L, Quaternion.identity, Pose.identity, samples, 300f, out var firstReason), Is.True, firstReason);
        Assert.That(coordinator.TryUpdateRotationPropagation(300_000_000L, Quaternion.identity, Pose.identity, samples, 300f, out var secondReason), Is.True, secondReason);

        var horizontal = coordinator.LatestHorizontalGnssSmoothingShadow.navigationDelta;
        var vertical = coordinator.ProductionVerticalAltitude.smoothedDeltaNavigationDown;
        var expectedWorldPosition = new Vector3(horizontal.x, horizontal.y, vertical);

        Assert.That(coordinator.ProductionHorizontalGnssApplied, Is.True);
        Assert.That(Mathf.Abs(vertical), Is.GreaterThan(0.000001f));
        Assert.That(Vector3.Distance(coordinator.Snapshot.worldFromCamera.position, expectedWorldPosition), Is.LessThan(Tolerance));
        Assert.That(Quaternion.Angle(coordinator.Snapshot.worldFromCamera.rotation, Quaternion.identity), Is.LessThan(Tolerance));
    }

    private static IReadOnlyList<TelemetryAircraftPositionSample> Samples(params TelemetryAircraftPositionSample[] samples) => samples;

    private static void AssertPose(Pose actual, Pose expected)
    {
        Assert.That(Vector3.Distance(actual.position, expected.position), Is.LessThan(Tolerance));
        Assert.That(Quaternion.Angle(actual.rotation, expected.rotation), Is.LessThan(Tolerance));
    }
}
