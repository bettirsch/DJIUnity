using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public sealed class DjiVisualRenderHandoverTests
{
    private const long Ms = 1_000_000L;
    private GameObject _object;
    private DjiWorldLocalizationShadow _shadow;
    private DjiWorldLocalizationShadowCoordinator _coordinator;
    private List<TelemetryAircraftPositionSample> _positions;

    [SetUp]
    public void SetUp()
    {
        _object = new GameObject("Visual handover lifecycle test");
        _shadow = _object.AddComponent<DjiWorldLocalizationShadow>();
        _coordinator = (DjiWorldLocalizationShadowCoordinator)typeof(DjiWorldLocalizationShadow)
            .GetField("_coordinator", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_shadow);
        _positions = new List<TelemetryAircraftPositionSample> { Position(100), Position(200), Position(300) };
        Assert.That(_coordinator.TryInitializeFromVisualMeasurement(Pose.identity, 100 * Ms, true, out var reason), Is.True, reason);
        Begin(100);
        Evaluate(200);
    }

    [TearDown]
    public void TearDown() => Object.DestroyImmediate(_object);

    [Test]
    public void SuccessfulHandover_StopsPropagationOnlyAtFinalRenderCommit()
    {
        AcceptUpstream(250);
        AssertPropagating();
        Evaluate(300);
        Assert.That(_shadow.TryCommitVisualRenderAuthority(Visual(250), 250 * Ms, out var reason), Is.True, reason);
        Assert.That(_shadow.HasActiveRotationPropagation, Is.False);
        Assert.That(_shadow.Snapshot.phase, Is.EqualTo(WorldLocalizationPhase.VisualLocalized));
        Assert.That(_shadow.Snapshot.lastVisualSourceTimestampNs, Is.EqualTo(250 * Ms));
        Assert.That(Vector3.Distance(_shadow.Snapshot.worldFromCamera.position, ReferenceFrameTransforms.Invert(Visual(250)).position), Is.LessThan(.0001f));
    }

    [Test]
    public void AbortedHandover_NextPendingCandidateDoesNotStopHorizontalOrVertical()
    {
        AcceptUpstream(250);
        // A subsequent pending/degraded candidate never reaches render commit.
        Evaluate(300);
        var before = _shadow.Snapshot.worldFromCamera;
        _positions.Add(Position(400));
        Evaluate(400);
        AssertPositionAdvances(before);
        Assert.That(_shadow.Snapshot.lastVisualSourceTimestampNs, Is.EqualTo(100 * Ms));
    }

    [Test]
    public void DelayedCommit_ContinuesTranslationWithoutRecapturingBaseline()
    {
        AcceptUpstream(250);
        Evaluate(300);
        var before = _shadow.Snapshot.worldFromCamera;
        for (var t = 400; t <= 2000; t += 100) { _positions.Add(Position(t)); Evaluate(t); }
        AssertPositionAdvances(before);
        Assert.That(_coordinator.LastProductionBaselineTimestampNs, Is.EqualTo(100 * Ms));
        AcceptUpstream(1990);
        Assert.That(_shadow.TryCommitVisualRenderAuthority(Visual(1990), 1990 * Ms, out var reason), Is.True, reason);
        Assert.That(_shadow.HasActiveRotationPropagation, Is.False);
    }

    [Test]
    public void RepeatedReacquisition_StartsIndependentNextOutageAndCommitsAgain()
    {
        AcceptUpstream(250);
        Assert.That(_shadow.TryCommitVisualRenderAuthority(Visual(250), 250 * Ms, out var reason), Is.True, reason);
        _positions.Add(Position(500));
        Begin(250);
        Evaluate(500);
        AcceptUpstream(550);
        _positions.Add(Position(600));
        Evaluate(600);
        AssertPropagating();
        Assert.That(_coordinator.LastProductionBaselineTimestampNs, Is.EqualTo(250 * Ms));
        Assert.That(_shadow.TryCommitVisualRenderAuthority(Visual(550), 550 * Ms, out reason), Is.True, reason);
        Assert.That(_shadow.Snapshot.lastVisualSourceTimestampNs, Is.EqualTo(550 * Ms));
        Assert.That(_shadow.HasActiveRotationPropagation, Is.False);
    }

    [Test]
    public void AuditedTwelvePointFourSevenSecondOrdering_NeverDisablesPropagatedTranslation()
    {
        Evaluate(300);
        var before = _shadow.Snapshot.worldFromCamera;
        AcceptUpstream(275);
        for (var t = 400; t <= 12_770; t += 10)
        {
            if (t % 100 == 0) _positions.Add(Position(t));
            Evaluate(t);
            AssertPropagating();
        }
        AssertPositionAdvances(before);
        Assert.That(_shadow.Snapshot.lastPropagationTimestampNs, Is.EqualTo(12_770 * Ms));
        Assert.That(_shadow.Snapshot.worldFromBoard.position, Is.EqualTo(Vector3.zero));
        Assert.That(_shadow.Snapshot.worldFromBoard.rotation, Is.EqualTo(Quaternion.identity));
    }

    [Test]
    public void RejectedRenderCommit_PreservesActivePropagationAndEstimatorOutput()
    {
        Evaluate(300);
        var before = _shadow.Snapshot;
        var invalid = new Pose(new Vector3(float.NaN, 0f, 0f), Quaternion.identity);
        Assert.That(_shadow.TryCommitVisualRenderAuthority(invalid, 350 * Ms, out _), Is.False);
        AssertPropagating();
        Assert.That(_shadow.Snapshot.worldFromCamera.position, Is.EqualTo(before.worldFromCamera.position));
        _positions.Add(Position(400)); Evaluate(400);
        AssertPositionAdvances(before.worldFromCamera);
    }

    [Test]
    public void DuplicateCommittedRenderSample_IsIdempotent()
    {
        Assert.That(_shadow.TryCommitVisualRenderAuthority(Visual(250), 250 * Ms, out _), Is.True);
        var count = _coordinator.AcceptedVisualMeasurementCount;
        Assert.That(_shadow.TryCommitVisualRenderAuthority(Visual(250), 250 * Ms, out _), Is.True);
        Assert.That(_coordinator.AcceptedVisualMeasurementCount, Is.EqualTo(count));
    }

    private void AcceptUpstream(int timestampMs) => typeof(DjiWorldLocalizationShadow)
        .GetMethod("OnAuthoritativeVisualPoseAccepted", BindingFlags.Instance | BindingFlags.NonPublic)
        .Invoke(_shadow, new object[] { Visual(timestampMs), timestampMs * Ms });

    private void Begin(int baselineMs)
    {
        var pose = baselineMs == 100 ? Pose.identity : Visual(baselineMs);
        Assert.That(_coordinator.TryBeginRotationPropagation(pose, baselineMs * Ms, Quaternion.identity,
            _positions, 300f, out var reason), Is.True, reason);
    }

    private void Evaluate(int timestampMs)
    {
        Assert.That(_coordinator.TryEvaluateRotationPropagation(timestampMs * Ms, 200 * Ms, Quaternion.identity,
            Pose.identity, _positions, 300f, out var reason), Is.True, reason);
    }

    private void AssertPropagating()
    {
        Assert.That(_shadow.HasActiveRotationPropagation, Is.True);
        Assert.That(_shadow.Snapshot.phase, Is.EqualTo(WorldLocalizationPhase.Propagating));
        Assert.That(_coordinator.HasActiveHorizontalGnssWorldShadow, Is.True);
    }

    private void AssertPositionAdvances(Pose before)
    {
        AssertPropagating();
        Assert.That(_shadow.Snapshot.worldFromCamera.position.x, Is.GreaterThan(before.position.x));
        Assert.That(_coordinator.ProductionVerticalAltitude.smoothedDeltaNavigationDown, Is.LessThan(0f));
        Assert.That(_coordinator.ProductionVerticalAltitude.sourceFresh, Is.True);
    }

    private static Pose Visual(int t) => new(new Vector3(.001f * t, -.1f, 1f), Quaternion.identity);
    private static TelemetryAircraftPositionSample Position(int t) =>
        new(t * Ms, 47d + (t - 100) * 1e-7d, 19d, 100d + (t - 100) * .001d);
}
