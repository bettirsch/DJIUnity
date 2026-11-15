using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public sealed class DjiPropagationEvaluationTests
{
    private const long Ms = 1_000_000L;
    private const float Tolerance = 0.0001f;

    [TestCase(0f, 0f, 0f)]
    [TestCase(0f, 90f, 0f)]
    [TestCase(17f, 34f, -23f)]
    public void HorizontalOnly_RetainsEveryMappedWorldComponent(float pitch, float yaw, float roll)
    {
        var alignment = Quaternion.Euler(pitch, yaw, roll);
        var visual = new Pose(Vector3.zero, Quaternion.Inverse(alignment));
        var samples = Positions();
        var coordinator = Begin(visual, samples);
        Evaluate(coordinator, 200, 200, Quaternion.identity, samples);
        Evaluate(coordinator, 300, 200, Quaternion.identity, samples);
        var navigationDelta = coordinator.LatestHorizontalGnssSmoothingShadow.navigationDelta;
        Assert.That(navigationDelta.x, Is.EqualTo(0.25f).Within(Tolerance));
        Assert.That(navigationDelta.z, Is.EqualTo(0f));
        AssertVector(coordinator.Snapshot.worldFromCamera.position, alignment * navigationDelta);
        AssertVector(coordinator.Snapshot.worldFromBoard.position, Vector3.zero);
        Assert.That(Quaternion.Angle(coordinator.Snapshot.worldFromBoard.rotation, Quaternion.identity), Is.LessThan(Tolerance));
    }

    [Test]
    public void PureNavigationVertical_WithAltitudeContributionDisabled_HasNoHorizontalMotion()
    {
        var alignment = Quaternion.Euler(0f, 90f, 0f);
        var visual = new Pose(Vector3.zero, Quaternion.Inverse(alignment));
        var samples = new[] { Position(100), Position(200, altitude: 101d) };
        var coordinator = Begin(visual, samples);
        Evaluate(coordinator, 200, 200, Quaternion.identity, samples);
        var arguments = new object[]
        {
            coordinator.LatestHorizontalGnssSmoothingShadow,
            default(VerticalAltitudeProductionSnapshot),
            new Pose(Vector3.zero, alignment),
            new Pose(Vector3.zero, alignment)
        };
        typeof(DjiWorldLocalizationShadowCoordinator)
            .GetMethod("ApplyProductionNavigationTranslation", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(coordinator, arguments);
        AssertVector(((Pose)arguments[3]).position, Vector3.zero);
    }

    [Test]
    public void RepeatedAttitudeTimestamp_WithAdvancingEvaluation_ContinuesSmoothing()
    {
        var samples = Positions();
        var coordinator = Begin(Pose.identity, samples);
        Evaluate(coordinator, 200, 200, Quaternion.Euler(0f, 10f, 0f), samples);
        Evaluate(coordinator, 300, 200, Quaternion.Euler(0f, 10f, 0f), samples);
        var previous = coordinator.Snapshot.worldFromCamera;
        Evaluate(coordinator, 350, 200, Quaternion.Euler(0f, 90f, 0f), samples);
        Assert.That(coordinator.Snapshot.worldFromCamera.position.x, Is.GreaterThan(previous.position.x));
        Assert.That(Quaternion.Angle(coordinator.Snapshot.worldFromCamera.rotation, previous.rotation), Is.LessThan(Tolerance));
        Assert.That(coordinator.PropagationEvaluationTimestampNs, Is.EqualTo(350 * Ms));
        Assert.That(coordinator.PropagationAttitudeTimestampNs, Is.EqualTo(200 * Ms));
        Assert.That(coordinator.PropagationAttitudeAgeMilliseconds, Is.EqualTo(150d));
        Assert.That(coordinator.RotationPropagationEquivalence.sampleCount, Is.EqualTo(1));
        Assert.That(coordinator.Snapshot.lastPropagationTimestampNs, Is.EqualTo(350 * Ms));
    }

    [Test]
    public void GnssNewerThanAttitude_IsSelectedCausallyAtEvaluationTime()
    {
        var samples = Positions();
        var coordinator = Begin(Pose.identity, samples);
        Evaluate(coordinator, 200, 200, Quaternion.identity, samples);
        Evaluate(coordinator, 350, 200, Quaternion.identity, samples);
        Assert.That(coordinator.LatestHorizontalGnssWorldShadow.gnssTargetTimestampNs, Is.EqualTo(300 * Ms));
        Assert.That(coordinator.LatestHorizontalGnssWorldShadow.gnssTargetAgeMilliseconds, Is.EqualTo(50d));
        Assert.That(samples[2].timestampNs, Is.EqualTo(300 * Ms));
        Assert.That(coordinator.LatestHorizontalGnssSmoothingShadow.estimate.rawGnssTargetTimestampNs, Is.EqualTo(300 * Ms));
    }

    [TestCase(300, true)]
    [TestCase(301, false)]
    public void GnssAndAltitudeFreshness_UseEvaluationTime_NotHeldAttitude(int sourceAgeMs, bool fresh)
    {
        var samples = new[] { Position(100), Position(200, 47.00001d, 101d) };
        var coordinator = Begin(Pose.identity, samples);
        Evaluate(coordinator, 200, 200, Quaternion.identity, samples);
        Evaluate(coordinator, 200 + sourceAgeMs, 200, Quaternion.identity, samples);
        Assert.That(coordinator.LatestHorizontalGnssWorldShadow.valid, Is.EqualTo(fresh));
        Assert.That(coordinator.ProductionVerticalAltitude.sourceFresh, Is.EqualTo(fresh));
    }

    [Test]
    public void StaleSources_FreezeWithoutExtrapolation_AndFreshPositionResumesWithoutReset()
    {
        var samples = Positions();
        var coordinator = Begin(Pose.identity, samples);
        Evaluate(coordinator, 200, 200, Quaternion.Euler(0f, 15f, 0f), samples);
        Evaluate(coordinator, 300, 200, Quaternion.Euler(0f, 15f, 0f), samples);
        var held = coordinator.Snapshot.worldFromCamera;
        Evaluate(coordinator, 601, 200, Quaternion.Euler(0f, 90f, 0f), samples);
        Evaluate(coordinator, 10_000, 200, Quaternion.Euler(0f, 90f, 0f), samples);
        AssertVector(coordinator.Snapshot.worldFromCamera.position, held.position);
        Assert.That(Quaternion.Angle(coordinator.Snapshot.worldFromCamera.rotation, held.rotation), Is.LessThan(Tolerance));
        samples.Add(Position(10_010, 47.00002d));
        Evaluate(coordinator, 10_020, 200, Quaternion.Euler(0f, 90f, 0f), samples);
        Assert.That(coordinator.Snapshot.worldFromCamera.position.x, Is.GreaterThan(held.position.x));
        Assert.That(Vector3.Distance(coordinator.Snapshot.worldFromCamera.position, held.position), Is.LessThanOrEqualTo(0.0501f));
        Assert.That(coordinator.Snapshot.lastVisualSourceTimestampNs, Is.EqualTo(100 * Ms));
        Assert.That(coordinator.Snapshot.phase, Is.EqualTo(WorldLocalizationPhase.Propagating));
    }

    [Test]
    public void LongHeldAttitude_WithFreshGnssAndAltitude_ContinuesPositionWithoutRotationalExtrapolation()
    {
        var samples = Positions();
        var coordinator = Begin(Pose.identity, samples);
        Evaluate(coordinator, 200, 200, Quaternion.Euler(0f, 12f, 0f), samples);
        var rotation = coordinator.Snapshot.worldFromCamera.rotation;
        samples.Add(Position(60_000, 47.00003d, 101d));
        Evaluate(coordinator, 60_010, 200, Quaternion.Euler(0f, 90f, 0f), samples);
        Assert.That(coordinator.ProductionVerticalAltitude.sourceFresh, Is.True);
        Assert.That(coordinator.ProductionVerticalAltitude.smoothedDeltaNavigationDown, Is.LessThan(0f));
        Assert.That(coordinator.LatestHorizontalGnssWorldShadow.gnssTargetTimestampNs, Is.EqualTo(60_000 * Ms));
        Assert.That(Quaternion.Angle(coordinator.Snapshot.worldFromCamera.rotation, rotation), Is.LessThan(Tolerance));
        Assert.That(coordinator.PropagationAttitudeTimestampNs, Is.EqualTo(200 * Ms));
    }

    [Test]
    public void DuplicateOrBackwardEvaluation_DoesNotMutateEstimatorOrWorld()
    {
        var samples = Positions();
        var coordinator = Begin(Pose.identity, samples);
        Evaluate(coordinator, 300, 200, Quaternion.identity, samples);
        var before = coordinator.Snapshot;
        var estimateTimestamp = coordinator.LatestHorizontalGnssSmoothingShadow.estimate.targetTimestampNs;
        foreach (var milliseconds in new[] { 300, 250 })
        {
            Assert.That(coordinator.TryEvaluateRotationPropagation(milliseconds * Ms, 200 * Ms, Quaternion.Euler(0f, 40f, 0f),
                Pose.identity, samples, 300f, out var reason), Is.False);
            Assert.That(reason, Is.EqualTo("NON_INCREASING_PROPAGATION_TIMESTAMP"));
            AssertVector(coordinator.Snapshot.worldFromCamera.position, before.worldFromCamera.position);
            Assert.That(coordinator.LatestHorizontalGnssSmoothingShadow.estimate.targetTimestampNs, Is.EqualTo(estimateTimestamp));
        }
    }

    [Test]
    public void FutureAttitudeAndFuturePosition_AreNotAdmitted()
    {
        var samples = Positions();
        samples.Add(Position(500, 47.001d));
        var coordinator = Begin(Pose.identity, samples);
        Evaluate(coordinator, 300, 200, Quaternion.Euler(0f, 12f, 0f), samples);
        var rotation = coordinator.Snapshot.worldFromCamera.rotation;
        Evaluate(coordinator, 350, 500, Quaternion.Euler(0f, 90f, 0f), samples);
        Assert.That(coordinator.LatestHorizontalGnssWorldShadow.gnssTargetTimestampNs, Is.EqualTo(300 * Ms));
        Assert.That(coordinator.PropagationAttitudeTimestampNs, Is.EqualTo(200 * Ms));
        Assert.That(Quaternion.Angle(coordinator.Snapshot.worldFromCamera.rotation, rotation), Is.LessThan(Tolerance));
    }

    [Test]
    public void VisualReacquisition_StillImmediatelyReplacesPropagatedPoseAndPreservesBoard()
    {
        var samples = Positions();
        var coordinator = Begin(Pose.identity, samples);
        Evaluate(coordinator, 350, 200, Quaternion.Euler(0f, 12f, 0f), samples);
        var visual = new Pose(new Vector3(0.2f, -0.1f, 1f), Quaternion.Euler(4f, 8f, 2f));
        Assert.That(coordinator.TryUpdateFromVisualMeasurement(visual, 340 * Ms, true, samples, 300f, out var reason), Is.True, reason);
        AssertVector(coordinator.Snapshot.worldFromCamera.position, ReferenceFrameTransforms.Invert(visual).position);
        Assert.That(coordinator.Snapshot.phase, Is.EqualTo(WorldLocalizationPhase.VisualLocalized));
        AssertVector(coordinator.Snapshot.worldFromBoard.position, Vector3.zero);
        Assert.That(coordinator.PropagationEvaluationTimestampNs, Is.Zero);
    }

    private static DjiWorldLocalizationShadowCoordinator Begin(Pose visual, IReadOnlyList<TelemetryAircraftPositionSample> samples)
    {
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        Assert.That(coordinator.TryInitializeFromVisualMeasurement(visual, 100 * Ms, true, out var initializeReason), Is.True, initializeReason);
        Assert.That(coordinator.TryBeginRotationPropagation(visual, 100 * Ms, Quaternion.identity, samples, 300f, out var reason), Is.True, reason);
        return coordinator;
    }

    private static void Evaluate(DjiWorldLocalizationShadowCoordinator coordinator, long evaluationMs, long attitudeMs,
        Quaternion rotation, IReadOnlyList<TelemetryAircraftPositionSample> samples)
    {
        Assert.That(coordinator.TryEvaluateRotationPropagation(evaluationMs * Ms, attitudeMs * Ms, rotation,
            Pose.identity, samples, 300f, out var reason), Is.True, reason);
    }

    private static List<TelemetryAircraftPositionSample> Positions() => new()
    {
        Position(100), Position(200, 47.00001d), Position(300, 47.00002d)
    };

    private static TelemetryAircraftPositionSample Position(long milliseconds, double latitude = 47d, double altitude = 100d) =>
        new(milliseconds * Ms, latitude, 19d, altitude);

    private static void AssertVector(Vector3 actual, Vector3 expected) =>
        Assert.That(Vector3.Distance(actual, expected), Is.LessThan(Tolerance), $"actual={actual:F8} expected={expected:F8}");
}
