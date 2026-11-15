using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public sealed class DjiWorldLocalizationHorizontalGnssShadowTests
{
    private const float Tolerance = 0.0001f;

    [Test]
    public void HorizontalGnssShadow_MapsNedDeltaThroughFixedWorldNavigationAlignment()
    {
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var worldFromNavigation = Quaternion.Euler(15f, 35f, -10f);
        var cameraFromBoard = new Pose(Vector3.zero, Quaternion.Inverse(worldFromNavigation));
        var samples = Samples(
            new TelemetryAircraftPositionSample(100_000_000L, 47.0d, 19.0d, 100d),
            new TelemetryAircraftPositionSample(200_000_000L, 47.00001d, 19.00002d, 100d));

        Assert.That(coordinator.TryInitializeFromVisualMeasurement(cameraFromBoard, 100_000_000L, true, out var initializeReason), Is.True, initializeReason);
        Assert.That(coordinator.TryBeginRotationPropagation(
            cameraFromBoard,
            100_000_000L,
            Quaternion.identity,
            samples,
            300f,
            out var beginReason), Is.True, beginReason);

        Assert.That(coordinator.TryUpdateRotationPropagation(
            200_000_000L,
            Quaternion.identity,
            cameraFromBoard,
            samples,
            300f,
            out var updateReason), Is.True, updateReason);

        var snapshot = coordinator.LatestHorizontalGnssWorldShadow;
        var expectedHorizontal = DjiGeodesy.HorizontalNedDelta(47.0d, 19.0d, 47.00001d, 19.00002d);
        var expectedNavigation = new Vector3(expectedHorizontal.x, expectedHorizontal.y, 0f);
        Assert.That(snapshot.valid, Is.True);
        Assert.That(Vector3.Distance(snapshot.navigationDelta, expectedNavigation), Is.LessThan(Tolerance));
        Assert.That(Vector3.Distance(snapshot.worldDelta, worldFromNavigation * expectedNavigation), Is.LessThan(Tolerance));
        Assert.That(snapshot.navigationDelta.z, Is.EqualTo(0f));
        Assert.That(snapshot.cameraLeverArmApplied, Is.False);
        Assert.That(snapshot.hasWorldFromNavigationRotation, Is.True);
    }

    [Test]
    public void HorizontalGnssShadow_RejectsFutureAndStaleSamplesWithoutAffectingRotationShadow()
    {
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var visual = new Pose(new Vector3(0.1f, 0.2f, 1f), Quaternion.identity);
        var futureOnly = Samples(new TelemetryAircraftPositionSample(101_000_000L, 47d, 19d, 0d));

        Assert.That(coordinator.TryInitializeFromVisualMeasurement(visual, 100_000_000L, true, out var initializeReason), Is.True, initializeReason);
        Assert.That(coordinator.TryBeginRotationPropagation(
            visual, 100_000_000L, Quaternion.identity, futureOnly, 300f, out var beginReason), Is.True, beginReason);
        Assert.That(coordinator.LatestHorizontalGnssWorldShadow.failureReason, Is.EqualTo("NO_CAUSAL_GNSS_BASELINE"));

        var production = ReferenceFrameTransforms.PredictCameraFromBoardRotationOnly(
            visual, Quaternion.identity, Quaternion.Euler(0f, 15f, 0f));
        Assert.That(coordinator.TryUpdateRotationPropagation(
            500_000_000L,
            Quaternion.Euler(0f, 15f, 0f),
            production,
            futureOnly,
            300f,
            out var updateReason), Is.True, updateReason);
        Assert.That(coordinator.RotationPropagationEquivalence.sampleCount, Is.EqualTo(1));
        Assert.That(coordinator.Snapshot.phase, Is.EqualTo(WorldLocalizationPhase.Propagating));
        Assert.That(coordinator.LatestHorizontalGnssWorldShadow.valid, Is.False);
        Assert.That(coordinator.LatestHorizontalGnssWorldShadow.failureReason, Is.EqualTo("NO_CAUSAL_GNSS_BASELINE"));
    }

    [Test]
    public void HorizontalGnssShadow_RejectsTargetBeyondFreshnessCap()
    {
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var samples = Samples(new TelemetryAircraftPositionSample(100_000_000L, 47d, 19d, 0d));

        Assert.That(coordinator.TryInitializeFromVisualMeasurement(Pose.identity, 100_000_000L, true, out var initializeReason), Is.True, initializeReason);
        Assert.That(coordinator.TryBeginRotationPropagation(
            Pose.identity, 100_000_000L, Quaternion.identity, samples, 300f, out var beginReason), Is.True, beginReason);
        Assert.That(coordinator.TryUpdateRotationPropagation(
            401_000_000L, Quaternion.identity, Pose.identity, samples, 300f, out var updateReason), Is.True, updateReason);

        var snapshot = coordinator.LatestHorizontalGnssWorldShadow;
        Assert.That(snapshot.valid, Is.False);
        Assert.That(snapshot.failureReason, Is.EqualTo("GNSS_TARGET_STALE"));
        Assert.That(snapshot.gnssTargetTimestampNs, Is.EqualTo(100_000_000L));
    }

    [Test]
    public void HorizontalGnssShadow_RejectsStaleBaselineWithoutRejectingRotationPropagation()
    {
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var visual = new Pose(new Vector3(0f, 0f, 1f), Quaternion.identity);
        var staleBaseline = Samples(new TelemetryAircraftPositionSample(100_000_000L, 47d, 19d, 0d));

        Assert.That(coordinator.TryInitializeFromVisualMeasurement(visual, 500_000_000L, true, out var initializeReason), Is.True, initializeReason);
        Assert.That(coordinator.TryBeginRotationPropagation(
            visual, 500_000_000L, Quaternion.identity, staleBaseline, 300f, out var beginReason), Is.True, beginReason);

        Assert.That(coordinator.LatestHorizontalGnssWorldShadow.valid, Is.False);
        Assert.That(coordinator.LatestHorizontalGnssWorldShadow.failureReason, Is.EqualTo("GNSS_BASELINE_STALE"));
        Assert.That(coordinator.HasActiveRotationPropagation, Is.True);
    }

    [Test]
    public void HorizontalGnssShadow_SelectsLatestCausalTargetAndNeverFutureSample()
    {
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var samples = Samples(
            new TelemetryAircraftPositionSample(100_000_000L, 47d, 19d, 0d),
            new TelemetryAircraftPositionSample(180_000_000L, 47.00001d, 19d, 0d),
            new TelemetryAircraftPositionSample(220_000_000L, 48d, 20d, 0d));

        Assert.That(coordinator.TryInitializeFromVisualMeasurement(Pose.identity, 100_000_000L, true, out var initializeReason), Is.True, initializeReason);
        Assert.That(coordinator.TryBeginRotationPropagation(
            Pose.identity, 100_000_000L, Quaternion.identity, samples, 300f, out var beginReason), Is.True, beginReason);
        Assert.That(coordinator.TryUpdateRotationPropagation(
            200_000_000L, Quaternion.identity, Pose.identity, samples, 300f, out var updateReason), Is.True, updateReason);

        Assert.That(coordinator.LatestHorizontalGnssWorldShadow.valid, Is.True);
        Assert.That(coordinator.LatestHorizontalGnssWorldShadow.gnssTargetTimestampNs, Is.EqualTo(180_000_000L));
    }

    [Test]
    public void HorizontalGnssShadow_UsesExactHistoricalProductionBaselineForNPlusTwoLag()
    {
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var visualN = new Pose(new Vector3(0f, 0f, 1f), Quaternion.identity);
        var visualNPlusOne = new Pose(new Vector3(0.1f, 0f, 1f), Quaternion.identity);
        var visualNPlusTwo = new Pose(new Vector3(0.2f, 0f, 1f), Quaternion.identity);
        var samples = Samples(
            new TelemetryAircraftPositionSample(100_000_000L, 47d, 19d, 0d),
            new TelemetryAircraftPositionSample(400_000_000L, 47.00001d, 19d, 0d));

        Assert.That(coordinator.TryInitializeFromVisualMeasurement(visualN, 100_000_000L, true, out var initializeReason), Is.True, initializeReason);
        Assert.That(coordinator.TryUpdateFromVisualMeasurement(visualNPlusOne, 200_000_000L, true, out var updateOneReason), Is.True, updateOneReason);
        Assert.That(coordinator.TryUpdateFromVisualMeasurement(visualNPlusTwo, 300_000_000L, true, out var updateTwoReason), Is.True, updateTwoReason);
        Assert.That(coordinator.TryBeginRotationPropagation(
            visualN, 100_000_000L, Quaternion.identity, samples, 300f, out var beginReason), Is.True, beginReason);
        Assert.That(coordinator.TryUpdateRotationPropagation(
            400_000_000L, Quaternion.identity, visualN, samples, 300f, out var propagationReason), Is.True, propagationReason);

        var candidate = coordinator.LatestHorizontalGnssWorldShadow;
        Assert.That(coordinator.LastResolvedWorldBaselineTimestampNs, Is.EqualTo(100_000_000L));
        Assert.That(candidate.visualBaselineTimestampNs, Is.EqualTo(100_000_000L));
        Assert.That(candidate.gnssBaselineTimestampNs, Is.EqualTo(100_000_000L));
        Assert.That(candidate.valid, Is.True);
    }

    [Test]
    public void HorizontalGnssShadow_ReacquisitionReportsHorizontalResidualWithoutChangingWorldBoard()
    {
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var samples = Samples(
            new TelemetryAircraftPositionSample(100_000_000L, 47d, 19d, 0d),
            new TelemetryAircraftPositionSample(200_000_000L, 47.00001d, 19.00002d, 0d));
        Assert.That(coordinator.TryInitializeFromVisualMeasurement(Pose.identity, 100_000_000L, true, out var initializeReason), Is.True, initializeReason);
        var boardBefore = coordinator.Snapshot.worldFromBoard;
        Assert.That(coordinator.TryBeginRotationPropagation(
            Pose.identity, 100_000_000L, Quaternion.identity, samples, 300f, out var beginReason), Is.True, beginReason);
        Assert.That(coordinator.TryUpdateRotationPropagation(
            200_000_000L, Quaternion.identity, Pose.identity, samples, 300f, out var updateReason), Is.True, updateReason);
        var candidate = coordinator.LatestHorizontalGnssWorldShadow;
        var reacquiredWorldFromCamera = new Pose(candidate.candidateWorldFromCamera.position, Quaternion.identity);
        var reacquiredCameraFromBoard = ReferenceFrameTransforms.Invert(reacquiredWorldFromCamera);

        Assert.That(coordinator.TryUpdateFromVisualMeasurement(
            reacquiredCameraFromBoard,
            200_000_000L,
            true,
            samples,
            300f,
            out var reacquisitionReason), Is.True, reacquisitionReason);

        var comparison = coordinator.LatestHorizontalGnssWorldShadowReacquisition;
        Assert.That(comparison.hasComparison, Is.True);
        Assert.That(comparison.navigationHorizontalErrorMagnitudeMeters, Is.LessThan(Tolerance));
        Assert.That(Vector3.Distance(coordinator.Snapshot.worldFromBoard.position, boardBefore.position), Is.LessThan(Tolerance));
        Assert.That(coordinator.Snapshot.phase, Is.EqualTo(WorldLocalizationPhase.VisualLocalized));
    }

    [Test]
    public void HorizontalGnssShadow_SurvivesProductionTimeoutWithoutUpdatingWorldState()
    {
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var samples = Samples(
            new TelemetryAircraftPositionSample(100_000_000L, 47d, 19d, 0d),
            new TelemetryAircraftPositionSample(2_100_000_000L, 47.00001d, 19.00001d, 0d),
            new TelemetryAircraftPositionSample(5_000_000_000L, 47.00002d, 19.00002d, 0d));

        Assert.That(coordinator.TryInitializeFromVisualMeasurement(Pose.identity, 100_000_000L, true, out var initializeReason), Is.True, initializeReason);
        Assert.That(coordinator.TryBeginRotationPropagation(
            Pose.identity, 100_000_000L, Quaternion.identity, samples, 300f, out var beginReason), Is.True, beginReason);
        Assert.That(coordinator.TryUpdateRotationPropagation(
            2_100_000_000L, Quaternion.identity, Pose.identity, samples, 300f, out var updateReason), Is.True, updateReason);

        var worldBeforeTimeout = coordinator.Snapshot;
        coordinator.StopRotationPropagation();
        coordinator.UpdateHorizontalGnssWorldShadowDuringBoardHidden(
            5_000_000_000L, DjiRenderPoseSource.None, samples, 300f);

        var candidate = coordinator.LatestHorizontalGnssWorldShadow;
        Assert.That(coordinator.HasActiveRotationPropagation, Is.False);
        Assert.That(coordinator.HasActiveHorizontalGnssWorldShadow, Is.True);
        Assert.That(candidate.valid, Is.True);
        Assert.That(candidate.hasCandidateRotation, Is.False);
        Assert.That(candidate.visualBaselineTimestampNs, Is.EqualTo(100_000_000L));
        Assert.That(candidate.gnssTargetTimestampNs, Is.EqualTo(5_000_000_000L));
        Assert.That(Vector3.Distance(coordinator.Snapshot.worldFromCamera.position, worldBeforeTimeout.worldFromCamera.position), Is.LessThan(Tolerance));
        Assert.That(Quaternion.Angle(coordinator.Snapshot.worldFromCamera.rotation, worldBeforeTimeout.worldFromCamera.rotation), Is.LessThan(Tolerance));
        Assert.That(coordinator.HorizontalGnssWorldShadowDiagnostics.candidateValidWhileProductionNone, Is.True);
        Assert.That(coordinator.HorizontalGnssWorldShadowDiagnostics.productionRotationPropagationExpired, Is.True);
    }

    [Test]
    public void HorizontalGnssShadow_KeepsItsOriginalBaselineAcrossFifteenSecondOutage()
    {
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var samples = Samples(
            new TelemetryAircraftPositionSample(100_000_000L, 47d, 19d, 0d),
            new TelemetryAircraftPositionSample(5_000_000_000L, 47.00001d, 19.00001d, 0d),
            new TelemetryAircraftPositionSample(15_000_000_000L, 47.00003d, 19.00002d, 0d));

        Assert.That(coordinator.TryInitializeFromVisualMeasurement(Pose.identity, 100_000_000L, true, out var initializeReason), Is.True, initializeReason);
        Assert.That(coordinator.TryBeginRotationPropagation(
            Pose.identity, 100_000_000L, Quaternion.identity, samples, 300f, out var beginReason), Is.True, beginReason);
        coordinator.StopRotationPropagation();
        coordinator.UpdateHorizontalGnssWorldShadowDuringBoardHidden(
            5_000_000_000L, DjiRenderPoseSource.None, samples, 300f);
        coordinator.UpdateHorizontalGnssWorldShadowDuringBoardHidden(
            15_000_000_000L, DjiRenderPoseSource.None, samples, 300f);

        var candidate = coordinator.LatestHorizontalGnssWorldShadow;
        Assert.That(candidate.valid, Is.True);
        Assert.That(candidate.visualBaselineTimestampNs, Is.EqualTo(100_000_000L));
        Assert.That(candidate.gnssBaselineTimestampNs, Is.EqualTo(100_000_000L));
        Assert.That(candidate.gnssTargetTimestampNs, Is.EqualTo(15_000_000_000L));
        Assert.That(coordinator.HorizontalGnssWorldShadowDiagnostics.boardHiddenAgeMilliseconds, Is.EqualTo(14_900d).Within(0.001d));
    }

    [Test]
    public void HorizontalGnssShadow_TemporaryStalenessInvalidatesOnlyTheCandidateAndThenResumes()
    {
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var samples = Samples(
            new TelemetryAircraftPositionSample(100_000_000L, 47d, 19d, 0d),
            new TelemetryAircraftPositionSample(900_000_000L, 47.00001d, 19d, 0d));

        Assert.That(coordinator.TryInitializeFromVisualMeasurement(Pose.identity, 100_000_000L, true, out var initializeReason), Is.True, initializeReason);
        Assert.That(coordinator.TryBeginRotationPropagation(
            Pose.identity, 100_000_000L, Quaternion.identity, samples, 300f, out var beginReason), Is.True, beginReason);
        coordinator.StopRotationPropagation();
        coordinator.UpdateHorizontalGnssWorldShadowDuringBoardHidden(
            500_000_000L, DjiRenderPoseSource.None, samples, 300f);

        Assert.That(coordinator.LatestHorizontalGnssWorldShadow.valid, Is.False);
        Assert.That(coordinator.LatestHorizontalGnssWorldShadow.failureReason, Is.EqualTo("GNSS_TARGET_STALE"));
        Assert.That(coordinator.HasActiveHorizontalGnssWorldShadow, Is.True);
        Assert.That(coordinator.HorizontalGnssWorldShadowDiagnostics.staleTargetGapCount, Is.EqualTo(1));

        coordinator.UpdateHorizontalGnssWorldShadowDuringBoardHidden(
            900_000_000L, DjiRenderPoseSource.None, samples, 300f);
        Assert.That(coordinator.LatestHorizontalGnssWorldShadow.valid, Is.True);
        Assert.That(coordinator.LatestHorizontalGnssWorldShadow.gnssTargetTimestampNs, Is.EqualTo(900_000_000L));
        Assert.That(coordinator.HasActiveHorizontalGnssWorldShadow, Is.True);
    }

    [Test]
    public void HorizontalGnssShadow_NeverUsesFutureTargetAfterProductionTimeout()
    {
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var samples = Samples(
            new TelemetryAircraftPositionSample(100_000_000L, 47d, 19d, 0d),
            new TelemetryAircraftPositionSample(6_000_000_000L, 48d, 20d, 0d));

        Assert.That(coordinator.TryInitializeFromVisualMeasurement(Pose.identity, 100_000_000L, true, out var initializeReason), Is.True, initializeReason);
        Assert.That(coordinator.TryBeginRotationPropagation(
            Pose.identity, 100_000_000L, Quaternion.identity, samples, 300f, out var beginReason), Is.True, beginReason);
        coordinator.StopRotationPropagation();
        coordinator.UpdateHorizontalGnssWorldShadowDuringBoardHidden(
            5_000_000_000L, DjiRenderPoseSource.None, samples, 300f);

        Assert.That(coordinator.LatestHorizontalGnssWorldShadow.valid, Is.False);
        Assert.That(coordinator.LatestHorizontalGnssWorldShadow.failureReason, Is.EqualTo("GNSS_TARGET_STALE"));
        Assert.That(coordinator.LatestHorizontalGnssWorldShadow.gnssTargetTimestampNs, Is.EqualTo(100_000_000L));
    }

    [Test]
    public void HorizontalGnssShadow_ReacquisitionAfterNoneReportsResidualAndEndsDiagnosticOutage()
    {
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var samples = Samples(
            new TelemetryAircraftPositionSample(100_000_000L, 47d, 19d, 0d),
            new TelemetryAircraftPositionSample(5_000_000_000L, 47.00001d, 19.00002d, 0d));

        Assert.That(coordinator.TryInitializeFromVisualMeasurement(Pose.identity, 100_000_000L, true, out var initializeReason), Is.True, initializeReason);
        var boardBefore = coordinator.Snapshot.worldFromBoard;
        Assert.That(coordinator.TryBeginRotationPropagation(
            Pose.identity, 100_000_000L, Quaternion.identity, samples, 300f, out var beginReason), Is.True, beginReason);
        coordinator.StopRotationPropagation();
        coordinator.UpdateHorizontalGnssWorldShadowDuringBoardHidden(
            5_000_000_000L, DjiRenderPoseSource.None, samples, 300f);
        var candidate = coordinator.LatestHorizontalGnssWorldShadow;
        var reacquiredCameraFromBoard = ReferenceFrameTransforms.Invert(
            new Pose(candidate.candidateWorldFromCamera.position, Quaternion.identity));

        Assert.That(coordinator.TryUpdateFromVisualMeasurement(
            reacquiredCameraFromBoard, 5_000_000_000L, true, samples, 300f, out var reacquisitionReason), Is.True, reacquisitionReason);

        Assert.That(coordinator.LatestHorizontalGnssWorldShadowReacquisition.hasComparison, Is.True);
        Assert.That(coordinator.LatestHorizontalGnssWorldShadowReacquisition.navigationHorizontalErrorMagnitudeMeters, Is.LessThan(Tolerance));
        Assert.That(coordinator.HorizontalGnssWorldShadowDiagnostics.reacquisitionCandidateValid, Is.True);
        Assert.That(coordinator.HasActiveHorizontalGnssWorldShadow, Is.False);
        Assert.That(Vector3.Distance(coordinator.Snapshot.worldFromBoard.position, boardBefore.position), Is.LessThan(Tolerance));
    }

    [Test]
    public void HorizontalGnssShadow_ReacquisitionWithoutFreshCausalTargetDoesNotFabricateComparison()
    {
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var samples = Samples(new TelemetryAircraftPositionSample(100_000_000L, 47d, 19d, 0d));

        Assert.That(coordinator.TryInitializeFromVisualMeasurement(Pose.identity, 100_000_000L, true, out var initializeReason), Is.True, initializeReason);
        Assert.That(coordinator.TryBeginRotationPropagation(
            Pose.identity, 100_000_000L, Quaternion.identity, samples, 300f, out var beginReason), Is.True, beginReason);
        coordinator.StopRotationPropagation();
        coordinator.UpdateHorizontalGnssWorldShadowDuringBoardHidden(
            5_000_000_000L, DjiRenderPoseSource.None, samples, 300f);

        Assert.That(coordinator.TryUpdateFromVisualMeasurement(
            Pose.identity, 5_000_000_000L, true, samples, 300f, out var reacquisitionReason), Is.True, reacquisitionReason);

        Assert.That(coordinator.LatestHorizontalGnssWorldShadowReacquisition.hasComparison, Is.False);
        Assert.That(coordinator.HorizontalGnssWorldShadowDiagnostics.reacquisitionCandidateValid, Is.False);
        Assert.That(coordinator.HasActiveHorizontalGnssWorldShadow, Is.False);
    }

    private static List<TelemetryAircraftPositionSample> Samples(params TelemetryAircraftPositionSample[] samples) => new(samples);
}
