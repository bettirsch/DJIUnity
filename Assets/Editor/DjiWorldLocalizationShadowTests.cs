using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public sealed class DjiWorldLocalizationShadowTests
{
    private const float Tolerance = 0.0001f;

    [Test]
    public void NoVisualMeasurement_LeavesCoordinatorUninitialized()
    {
        var coordinator = new DjiWorldLocalizationShadowCoordinator();

        Assert.That(coordinator.Snapshot.hasWorld, Is.False);
        Assert.That(coordinator.Snapshot.phase, Is.EqualTo(WorldLocalizationPhase.Uninitialized));
    }

    [Test]
    public void RejectedOrPendingVisualMeasurement_DoesNotInitialize()
    {
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var pose = new Pose(new Vector3(0.1f, 0.2f, 0.3f), Quaternion.Euler(5f, 10f, 15f));

        Assert.That(
            coordinator.TryInitializeFromVisualMeasurement(pose, 1L, isAuthoritativeVisual: false, out var reason),
            Is.False);
        Assert.That(reason, Is.EqualTo("NOT_AUTHORITATIVE_VISUAL"));
        Assert.That(coordinator.Snapshot.hasWorld, Is.False);
    }

    [Test]
    public void FirstAuthoritativeVisualMeasurement_InitializesShadowExactlyOnce()
    {
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var cameraFromBoard = new Pose(
            new Vector3(0.4f, -0.2f, 1.1f),
            Quaternion.Euler(11f, -23f, 7f));

        Assert.That(
            coordinator.TryInitializeFromVisualMeasurement(cameraFromBoard, 42L, true, out var reason),
            Is.True,
            reason);

        var snapshot = coordinator.Snapshot;
        Assert.That(snapshot.hasWorld, Is.True);
        Assert.That(snapshot.phase, Is.EqualTo(WorldLocalizationPhase.VisualLocalized));
        Assert.That(snapshot.lastVisualSourceTimestampNs, Is.EqualTo(42L));
        AssertPose(snapshot.worldFromBoard, Pose.identity);
        AssertWorldCompositionInvariant(snapshot, cameraFromBoard);
    }

    [Test]
    public void SecondNewerAuthoritativeVisualMeasurement_UpdatesCameraWithoutReplacingWorldBoard()
    {
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var firstPose = new Pose(new Vector3(0.3f, -0.4f, 1.2f), Quaternion.Euler(5f, 25f, -10f));
        var secondPose = new Pose(new Vector3(-1.2f, 0.8f, 0.1f), Quaternion.Euler(-20f, 80f, 30f));

        Assert.That(coordinator.TryInitializeFromVisualMeasurement(firstPose, 100L, true, out var firstReason), Is.True, firstReason);
        var firstSnapshot = coordinator.Snapshot;

        Assert.That(coordinator.TryUpdateFromVisualMeasurement(secondPose, 200L, true, out var secondReason), Is.True, secondReason);
        var secondSnapshot = coordinator.Snapshot;

        AssertPose(secondSnapshot.worldFromBoard, firstSnapshot.worldFromBoard);
        AssertPose(secondSnapshot.worldFromCamera, ReferenceFrameTransforms.Invert(secondPose));
        Assert.That(secondSnapshot.lastVisualSourceTimestampNs, Is.EqualTo(200L));
        AssertWorldCompositionInvariant(secondSnapshot, secondPose);
        Assert.That(coordinator.AcceptedVisualMeasurementCount, Is.EqualTo(2));
    }

    [Test]
    public void NewCoordinator_StartsAsFreshDroneViewSession()
    {
        var firstSession = new DjiWorldLocalizationShadowCoordinator();
        Assert.That(firstSession.TryInitializeFromVisualMeasurement(Pose.identity, 1L, true, out var reason), Is.True, reason);

        var secondSession = new DjiWorldLocalizationShadowCoordinator();

        Assert.That(secondSession.Snapshot.hasWorld, Is.False);
        Assert.That(secondSession.Snapshot.phase, Is.EqualTo(WorldLocalizationPhase.Uninitialized));
    }

    [Test]
    public void DuplicateAndOlderAuthoritativeMeasurements_DoNotUpdateWorldCamera()
    {
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var firstPose = new Pose(new Vector3(0.1f, 0.2f, 0.3f), Quaternion.Euler(1f, 2f, 3f));
        var secondPose = new Pose(new Vector3(-0.4f, 0.5f, 0.6f), Quaternion.Euler(-4f, 5f, 6f));
        Assert.That(coordinator.TryInitializeFromVisualMeasurement(firstPose, 100L, true, out var initializeReason), Is.True, initializeReason);
        Assert.That(coordinator.TryUpdateFromVisualMeasurement(secondPose, 200L, true, out var updateReason), Is.True, updateReason);
        var acceptedSnapshot = coordinator.Snapshot;

        Assert.That(coordinator.TryUpdateFromVisualMeasurement(Pose.identity, 200L, true, out var duplicateReason), Is.False);
        Assert.That(duplicateReason, Is.EqualTo("NON_INCREASING_SOURCE_TIMESTAMP"));
        Assert.That(coordinator.TryUpdateFromVisualMeasurement(Pose.identity, 199L, true, out var olderReason), Is.False);
        Assert.That(olderReason, Is.EqualTo("NON_INCREASING_SOURCE_TIMESTAMP"));
        AssertPose(coordinator.Snapshot.worldFromBoard, acceptedSnapshot.worldFromBoard);
        AssertPose(coordinator.Snapshot.worldFromCamera, acceptedSnapshot.worldFromCamera);
        Assert.That(coordinator.AcceptedVisualMeasurementCount, Is.EqualTo(2));
    }

    [Test]
    public void RejectedVisualMeasurement_DoesNotUpdateInitializedShadow()
    {
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var firstPose = new Pose(new Vector3(0.1f, 0.2f, 0.3f), Quaternion.identity);
        Assert.That(coordinator.TryInitializeFromVisualMeasurement(firstPose, 100L, true, out var initializeReason), Is.True, initializeReason);
        var acceptedSnapshot = coordinator.Snapshot;

        Assert.That(coordinator.TryUpdateFromVisualMeasurement(Pose.identity, 200L, false, out var reason), Is.False);
        Assert.That(reason, Is.EqualTo("NOT_AUTHORITATIVE_VISUAL"));
        AssertPose(coordinator.Snapshot.worldFromBoard, acceptedSnapshot.worldFromBoard);
        AssertPose(coordinator.Snapshot.worldFromCamera, acceptedSnapshot.worldFromCamera);
    }

    [Test]
    public void UpdateBeforeInitialization_IsRejectedWithoutCreatingWorld()
    {
        var coordinator = new DjiWorldLocalizationShadowCoordinator();

        Assert.That(coordinator.TryUpdateFromVisualMeasurement(Pose.identity, 1L, true, out var reason), Is.False);
        Assert.That(reason, Is.EqualTo("WORLD_NOT_INITIALIZED"));
        Assert.That(coordinator.Snapshot.hasWorld, Is.False);
        Assert.That(coordinator.Snapshot.phase, Is.EqualTo(WorldLocalizationPhase.Uninitialized));
    }

    [Test]
    public void RotationPropagation_MirrorsProductionPredictionWithoutChangingWorldBoard()
    {
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var visual = new Pose(new Vector3(0.2f, -0.4f, 1.1f), Quaternion.Euler(5f, 10f, -3f));
        var navigationBaseline = Quaternion.Euler(2f, 15f, 4f);
        var navigationTarget = Quaternion.Euler(12f, -38f, 21f);
        Assert.That(coordinator.TryInitializeFromVisualMeasurement(visual, 100L, true, out var initializeReason), Is.True, initializeReason);
        var board = coordinator.Snapshot.worldFromBoard;
        Assert.That(coordinator.TryBeginRotationPropagation(visual, 100L, navigationBaseline, out var beginReason), Is.True, beginReason);
        var production = ReferenceFrameTransforms.PredictCameraFromBoardRotationOnly(visual, navigationBaseline, navigationTarget);

        Assert.That(coordinator.TryUpdateRotationPropagation(200L, navigationTarget, production, out var updateReason), Is.True, updateReason);

        Assert.That(coordinator.Snapshot.phase, Is.EqualTo(WorldLocalizationPhase.Propagating));
        AssertPose(coordinator.Snapshot.worldFromBoard, board);
        Assert.That(coordinator.RotationPropagationEquivalence.sampleCount, Is.EqualTo(1));
        Assert.That(coordinator.RotationPropagationEquivalence.latestTranslationResidualM, Is.LessThanOrEqualTo(Tolerance));
        Assert.That(coordinator.RotationPropagationEquivalence.latestRotationResidualDeg, Is.LessThanOrEqualTo(Tolerance));
        AssertWorldCompositionInvariant(coordinator.Snapshot, production);
    }

    [TestCase(0f, 30f, 0f)]
    [TestCase(0f, -30f, 0f)]
    [TestCase(17f, 0f, 0f)]
    [TestCase(0f, 0f, -21f)]
    public void RotationPropagation_UsesSharedYawPitchRollTransformOrder(
        float pitchDegrees,
        float yawDegrees,
        float rollDegrees)
    {
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var visual = new Pose(new Vector3(0.3f, -0.2f, 1.5f), Quaternion.Euler(5f, 6f, 7f));
        var baseline = Quaternion.Euler(3f, -4f, 2f);
        var target = Quaternion.Euler(pitchDegrees, yawDegrees, rollDegrees) * baseline;
        Assert.That(coordinator.TryInitializeFromVisualMeasurement(visual, 100L, true, out var initializeReason), Is.True, initializeReason);
        Assert.That(coordinator.TryBeginRotationPropagation(visual, 100L, baseline, out var beginReason), Is.True, beginReason);
        var production = ReferenceFrameTransforms.PredictCameraFromBoardRotationOnly(visual, baseline, target);

        Assert.That(coordinator.TryUpdateRotationPropagation(200L, target, production, out var updateReason), Is.True, updateReason);

        AssertWorldCompositionInvariant(coordinator.Snapshot, production);
        Assert.That(coordinator.RotationPropagationEquivalence.latestTranslationResidualM, Is.LessThanOrEqualTo(Tolerance));
        Assert.That(coordinator.RotationPropagationEquivalence.latestRotationResidualDeg, Is.LessThanOrEqualTo(Tolerance));
    }

    [Test]
    public void RotationPropagation_MultipleSamplesUseOriginalBaselineWithoutAccumulation()
    {
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var visual = new Pose(new Vector3(0.4f, 0.5f, 1.2f), Quaternion.Euler(7f, -11f, 13f));
        var baseline = Quaternion.Euler(3f, 8f, -5f);
        var firstTarget = Quaternion.Euler(15f, 35f, 20f);
        var secondTarget = Quaternion.Euler(-18f, 52f, 31f);
        Assert.That(coordinator.TryInitializeFromVisualMeasurement(visual, 100L, true, out var initializeReason), Is.True, initializeReason);
        Assert.That(coordinator.TryBeginRotationPropagation(visual, 100L, baseline, out var beginReason), Is.True, beginReason);
        Assert.That(coordinator.TryUpdateRotationPropagation(200L, firstTarget,
            ReferenceFrameTransforms.PredictCameraFromBoardRotationOnly(visual, baseline, firstTarget), out var firstReason), Is.True, firstReason);
        var expected = ReferenceFrameTransforms.PredictCameraFromBoardRotationOnly(visual, baseline, secondTarget);

        Assert.That(coordinator.TryUpdateRotationPropagation(300L, secondTarget, expected, out var secondReason), Is.True, secondReason);

        AssertWorldCompositionInvariant(coordinator.Snapshot, expected);
        Assert.That(coordinator.RotationPropagationEquivalence.sampleCount, Is.EqualTo(2));
    }

    [Test]
    public void RotationPropagation_StopPreventsFurtherUpdatesUntilNewBaseline()
    {
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        Assert.That(coordinator.TryInitializeFromVisualMeasurement(Pose.identity, 100L, true, out var initializeReason), Is.True, initializeReason);
        Assert.That(coordinator.TryBeginRotationPropagation(Pose.identity, 100L, Quaternion.identity, out var beginReason), Is.True, beginReason);
        coordinator.StopRotationPropagation();

        Assert.That(coordinator.TryUpdateRotationPropagation(200L, Quaternion.Euler(0f, 10f, 0f), Pose.identity, out var reason), Is.False);
        Assert.That(reason, Is.EqualTo("PROPAGATION_NOT_ACTIVE"));
        Assert.That(coordinator.Snapshot.phase, Is.EqualTo(WorldLocalizationPhase.VisualLocalized));
    }

    [Test]
    public void RotationPropagation_VisualReacquisitionReturnsToVisualLocalized()
    {
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var visual = new Pose(new Vector3(0f, 0f, 1f), Quaternion.identity);
        Assert.That(coordinator.TryInitializeFromVisualMeasurement(visual, 100L, true, out var initializeReason), Is.True, initializeReason);
        Assert.That(coordinator.TryBeginRotationPropagation(visual, 100L, Quaternion.identity, out var beginReason), Is.True, beginReason);
        var predicted = ReferenceFrameTransforms.PredictCameraFromBoardRotationOnly(visual, Quaternion.identity, Quaternion.Euler(0f, 20f, 0f));
        Assert.That(coordinator.TryUpdateRotationPropagation(200L, Quaternion.Euler(0f, 20f, 0f), predicted, out var propagationReason), Is.True, propagationReason);

        Assert.That(coordinator.TryUpdateFromVisualMeasurement(visual, 300L, true, out var visualReason), Is.True, visualReason);

        Assert.That(coordinator.Snapshot.phase, Is.EqualTo(WorldLocalizationPhase.VisualLocalized));
        Assert.That(coordinator.HasActiveRotationPropagation, Is.False);
    }

    [Test]
    public void RotationPropagation_ExactLatestProductionBaselineResolvesFromWorldHistory()
    {
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var visual = new Pose(new Vector3(0.1f, -0.2f, 1.3f), Quaternion.Euler(3f, 14f, -7f));

        Assert.That(coordinator.TryInitializeFromVisualMeasurement(visual, 100L, true, out var initializeReason), Is.True, initializeReason);
        Assert.That(coordinator.TryBeginRotationPropagation(visual, 100L, Quaternion.identity, out var beginReason), Is.True, beginReason);

        Assert.That(coordinator.AuthoritativeVisualHistoryCount, Is.EqualTo(1));
        Assert.That(coordinator.LastProductionBaselineTimestampNs, Is.EqualTo(100L));
        Assert.That(coordinator.LastResolvedWorldBaselineTimestampNs, Is.EqualTo(100L));
        Assert.That(coordinator.LastHistoricalBaselineResolved, Is.True);
    }

    [Test]
    public void RotationPropagation_OneFrameProductionBaselineLagUsesExactHistoricalWorldVisual()
    {
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var visualN = new Pose(new Vector3(0.2f, -0.3f, 1.1f), Quaternion.Euler(4f, 12f, -6f));
        var visualNPlusOne = new Pose(new Vector3(0.25f, -0.3f, 1.1f), Quaternion.Euler(5f, 18f, -6f));
        var navigationTarget = Quaternion.Euler(0f, 32f, 0f);

        Assert.That(coordinator.TryInitializeFromVisualMeasurement(visualN, 100L, true, out var initializeReason), Is.True, initializeReason);
        Assert.That(coordinator.TryUpdateFromVisualMeasurement(visualNPlusOne, 200L, true, out var updateReason), Is.True, updateReason);
        var worldBoard = coordinator.Snapshot.worldFromBoard;
        Assert.That(coordinator.TryBeginRotationPropagation(visualN, 100L, Quaternion.identity, out var beginReason), Is.True, beginReason);
        var production = ReferenceFrameTransforms.PredictCameraFromBoardRotationOnly(visualN, Quaternion.identity, navigationTarget);

        Assert.That(coordinator.TryUpdateRotationPropagation(300L, navigationTarget, production, out var propagationReason), Is.True, propagationReason);

        Assert.That(coordinator.LastResolvedWorldBaselineTimestampNs, Is.EqualTo(100L));
        Assert.That(coordinator.Snapshot.lastVisualSourceTimestampNs, Is.EqualTo(200L));
        AssertPose(coordinator.Snapshot.worldFromBoard, worldBoard);
        AssertWorldCompositionInvariant(coordinator.Snapshot, production);
        Assert.That(coordinator.RotationPropagationEquivalence.latestTranslationResidualM, Is.LessThanOrEqualTo(Tolerance));
        Assert.That(coordinator.RotationPropagationEquivalence.latestRotationResidualDeg, Is.LessThanOrEqualTo(Tolerance));
    }

    [Test]
    public void RotationPropagation_TwoFrameProductionBaselineLagUsesExactHistoricalWorldVisual()
    {
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var visualN = new Pose(new Vector3(-0.3f, 0.1f, 1.2f), Quaternion.Euler(-2f, 9f, 5f));
        var visualNPlusOne = new Pose(new Vector3(-0.25f, 0.15f, 1.2f), Quaternion.Euler(-1f, 13f, 5f));
        var visualNPlusTwo = new Pose(new Vector3(-0.2f, 0.2f, 1.2f), Quaternion.Euler(0f, 18f, 5f));
        var navigationTarget = Quaternion.Euler(7f, -28f, 11f);

        Assert.That(coordinator.TryInitializeFromVisualMeasurement(visualN, 100L, true, out var initializeReason), Is.True, initializeReason);
        Assert.That(coordinator.TryUpdateFromVisualMeasurement(visualNPlusOne, 200L, true, out var firstUpdateReason), Is.True, firstUpdateReason);
        Assert.That(coordinator.TryUpdateFromVisualMeasurement(visualNPlusTwo, 300L, true, out var secondUpdateReason), Is.True, secondUpdateReason);
        Assert.That(coordinator.TryBeginRotationPropagation(visualN, 100L, Quaternion.identity, out var beginReason), Is.True, beginReason);
        var production = ReferenceFrameTransforms.PredictCameraFromBoardRotationOnly(visualN, Quaternion.identity, navigationTarget);

        Assert.That(coordinator.TryUpdateRotationPropagation(400L, navigationTarget, production, out var propagationReason), Is.True, propagationReason);

        Assert.That(coordinator.LastResolvedWorldBaselineTimestampNs, Is.EqualTo(100L));
        Assert.That(coordinator.RotationPropagationEquivalence.worldBaselineHistoryAgeMs, Is.EqualTo(0.0002f).Within(Tolerance));
        Assert.That(coordinator.RotationPropagationEquivalence.latestTranslationResidualM, Is.LessThanOrEqualTo(Tolerance));
        Assert.That(coordinator.RotationPropagationEquivalence.latestRotationResidualDeg, Is.LessThanOrEqualTo(Tolerance));
    }

    [Test]
    public void RotationPropagation_MissingExactProductionBaselineRejectsWithoutNearestSubstitution()
    {
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        Assert.That(coordinator.TryInitializeFromVisualMeasurement(Pose.identity, 100L, true, out var initializeReason), Is.True, initializeReason);
        Assert.That(coordinator.TryUpdateFromVisualMeasurement(
            new Pose(new Vector3(0.1f, 0f, 1f), Quaternion.identity), 200L, true, out var updateReason), Is.True, updateReason);

        Assert.That(coordinator.TryBeginRotationPropagation(Pose.identity, 150L, Quaternion.identity, out var beginReason), Is.False);

        Assert.That(beginReason, Is.EqualTo("PRODUCTION_BASELINE_NOT_IN_WORLD_HISTORY"));
        Assert.That(coordinator.LastHistoricalBaselineResolved, Is.False);
        Assert.That(coordinator.LastResolvedWorldBaselineTimestampNs, Is.EqualTo(0L));
        Assert.That(coordinator.LastBaselineResolveFailureReason, Is.EqualTo("PRODUCTION_BASELINE_NOT_IN_WORLD_HISTORY"));
        Assert.That(coordinator.HasActiveRotationPropagation, Is.False);
    }

    [Test]
    public void RotationPropagation_AuthoritativeVisualHistoryIsBoundedAndNeverUsesEvictedBaseline()
    {
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var firstVisual = new Pose(new Vector3(0f, 0f, 1f), Quaternion.identity);
        Assert.That(coordinator.TryInitializeFromVisualMeasurement(firstVisual, 100L, true, out var initializeReason), Is.True, initializeReason);

        for (var index = 1; index <= 32; index++)
        {
            var timestampNs = 100L + index * 100L;
            var visual = new Pose(new Vector3(index * 0.01f, 0f, 1f), Quaternion.Euler(0f, index, 0f));
            Assert.That(coordinator.TryUpdateFromVisualMeasurement(visual, timestampNs, true, out var updateReason), Is.True, updateReason);
        }

        Assert.That(coordinator.AuthoritativeVisualHistoryCount, Is.EqualTo(32));
        Assert.That(coordinator.TryBeginRotationPropagation(firstVisual, 100L, Quaternion.identity, out var beginReason), Is.False);
        Assert.That(beginReason, Is.EqualTo("PRODUCTION_BASELINE_NOT_IN_WORLD_HISTORY"));
    }

    [Test]
    public void RotationPropagation_ReacquisitionHistoryRetainsOlderCompatibleProductionBaseline()
    {
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var visualN = new Pose(new Vector3(0f, 0f, 1f), Quaternion.identity);
        var reacquiredVisual = new Pose(new Vector3(0.1f, 0f, 1f), Quaternion.Euler(0f, 10f, 0f));
        var laterVisual = new Pose(new Vector3(0.2f, 0f, 1f), Quaternion.Euler(0f, 20f, 0f));
        Assert.That(coordinator.TryInitializeFromVisualMeasurement(visualN, 100L, true, out var initializeReason), Is.True, initializeReason);
        Assert.That(coordinator.TryBeginRotationPropagation(visualN, 100L, Quaternion.identity, out var firstBeginReason), Is.True, firstBeginReason);
        Assert.That(coordinator.TryUpdateRotationPropagation(200L, Quaternion.Euler(0f, 15f, 0f),
            ReferenceFrameTransforms.PredictCameraFromBoardRotationOnly(visualN, Quaternion.identity, Quaternion.Euler(0f, 15f, 0f)), out var propagationReason), Is.True, propagationReason);
        Assert.That(coordinator.TryUpdateFromVisualMeasurement(reacquiredVisual, 300L, true, out var reacquisitionReason), Is.True, reacquisitionReason);
        Assert.That(coordinator.TryUpdateFromVisualMeasurement(laterVisual, 400L, true, out var laterVisualReason), Is.True, laterVisualReason);
        var board = coordinator.Snapshot.worldFromBoard;

        Assert.That(coordinator.TryBeginRotationPropagation(reacquiredVisual, 300L, Quaternion.identity, out var secondBeginReason), Is.True, secondBeginReason);
        var production = ReferenceFrameTransforms.PredictCameraFromBoardRotationOnly(reacquiredVisual, Quaternion.identity, Quaternion.Euler(0f, 35f, 0f));
        Assert.That(coordinator.TryUpdateRotationPropagation(500L, Quaternion.Euler(0f, 35f, 0f), production, out var secondPropagationReason), Is.True, secondPropagationReason);

        Assert.That(coordinator.LastResolvedWorldBaselineTimestampNs, Is.EqualTo(300L));
        AssertPose(coordinator.Snapshot.worldFromBoard, board);
        AssertWorldCompositionInvariant(coordinator.Snapshot, production);
    }

    [Test]
    public void HorizontalGnssShadow_SurvivesNoneAndDoesNotMutateAuthoritativeWorld()
    {
        const long millisecond = 1_000_000L;
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var visual = Pose.identity;
        var positions = new List<TelemetryAircraftPositionSample>
        {
            new(100L * millisecond, 47d, 19d, 0d),
            new(200L * millisecond, 47.000001d, 19d, 0d),
            new(1000L * millisecond, 47.000002d, 19d, 0d)
        };
        Assert.That(coordinator.TryInitializeFromVisualMeasurement(visual, 100L * millisecond, true, out var initializeReason), Is.True, initializeReason);
        Assert.That(coordinator.TryBeginRotationPropagation(
            visual, 100L * millisecond, Quaternion.identity, positions, 300f, out var beginReason), Is.True, beginReason);
        Assert.That(coordinator.TryUpdateRotationPropagation(
            200L * millisecond, Quaternion.identity, visual, positions, 300f, out var updateReason), Is.True, updateReason);

        var beforeNone = coordinator.Snapshot;
        coordinator.UpdateHorizontalGnssWorldShadowDuringBoardHidden(
            1000L * millisecond, DjiRenderPoseSource.None, positions, 300f);

        Assert.That(coordinator.LatestHorizontalGnssWorldShadow.valid, Is.True);
        AssertPose(coordinator.Snapshot.worldFromCamera, beforeNone.worldFromCamera);
        AssertPose(coordinator.Snapshot.worldFromBoard, beforeNone.worldFromBoard);
    }

    [Test]
    public void HorizontalGnssSmoothingShadow_SurvivesNoneAndDoesNotMutateAuthoritativeWorld()
    {
        const long millisecond = 1_000_000L;
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var visual = Pose.identity;
        var positions = new List<TelemetryAircraftPositionSample>
        {
            new(100L * millisecond, 47d, 19d, 0d),
            new(200L * millisecond, 47.000001d, 19d, 0d),
            new(1000L * millisecond, 47.000002d, 19d, 0d)
        };

        Assert.That(coordinator.TryInitializeFromVisualMeasurement(visual, 100L * millisecond, true, out var initializeReason), Is.True, initializeReason);
        Assert.That(coordinator.TryBeginRotationPropagation(
            visual, 100L * millisecond, Quaternion.identity, positions, 300f, out var beginReason), Is.True, beginReason);
        Assert.That(coordinator.TryUpdateRotationPropagation(
            200L * millisecond, Quaternion.identity, visual, positions, 300f, out var updateReason), Is.True, updateReason);

        var beforeNone = coordinator.Snapshot;
        coordinator.UpdateHorizontalGnssWorldShadowDuringBoardHidden(
            1000L * millisecond, DjiRenderPoseSource.None, positions, 300f);

        Assert.That(coordinator.LatestHorizontalGnssSmoothingShadow.valid, Is.True);
        Assert.That(coordinator.LatestHorizontalGnssSmoothingShadow.estimate.state,
            Is.EqualTo(HorizontalGnssSmoothingState.FollowingFreshGnss));
        Assert.That(coordinator.LatestHorizontalGnssSmoothingShadow.navigationDelta.z, Is.EqualTo(0f));
        AssertPose(coordinator.Snapshot.worldFromCamera, beforeNone.worldFromCamera);
        AssertPose(coordinator.Snapshot.worldFromBoard, beforeNone.worldFromBoard);
    }

    [Test]
    public void HorizontalGnssSmoothingShadow_ReacquisitionRecordsRawSmoothedAndHeldResiduals()
    {
        const long millisecond = 1_000_000L;
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var visual = Pose.identity;
        var reacquiredVisual = new Pose(new Vector3(0.1f, 0f, 0f), Quaternion.identity);
        var positions = new List<TelemetryAircraftPositionSample>
        {
            // The raw shadow baseline is intentionally strictly causal to the visual baseline.
            new(99L * millisecond, 47d, 19d, 0d),
            new(200L * millisecond, 47.000001d, 19d, 0d),
            new(300L * millisecond, 47.000002d, 19d, 0d)
        };

        Assert.That(coordinator.TryInitializeFromVisualMeasurement(visual, 100L * millisecond, true, out var initializeReason), Is.True, initializeReason);
        Assert.That(coordinator.TryBeginRotationPropagation(
            visual, 100L * millisecond, Quaternion.identity, positions, 300f, out var beginReason), Is.True, beginReason);
        Assert.That(coordinator.TryUpdateRotationPropagation(
            200L * millisecond, Quaternion.identity, visual, positions, 300f, out var updateReason), Is.True, updateReason);
        Assert.That(coordinator.TryUpdateFromVisualMeasurement(
            reacquiredVisual, 300L * millisecond, true, positions, 300f, out var reacquisitionReason), Is.True, reacquisitionReason);

        var diagnostics = coordinator.LatestHorizontalGnssSmoothingReacquisition;
        Assert.That(diagnostics.hasComparison, Is.True);
        Assert.That(diagnostics.rawGnssHorizontalResidualMeters, Is.Not.NaN);
        Assert.That(diagnostics.smoothedHorizontalResidualMeters, Is.Not.NaN);
        Assert.That(diagnostics.heldPositionHorizontalResidualMeters, Is.Not.NaN);
    }

    [Test]
    public void RotationPropagation_FreshGnssPromotesSmoothedHorizontalWorldPositionAndPreservesNavigationVertical()
    {
        const long millisecond = 1_000_000L;
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var visual = new Pose(new Vector3(0f, 0f, 1.25f), Quaternion.Euler(3f, 8f, -2f));
        var positions = new List<TelemetryAircraftPositionSample>
        {
            new(99L * millisecond, 47d, 19d, 0d),
            new(200L * millisecond, 47.00001d, 19.00001d, 0d),
            new(300L * millisecond, 47.00002d, 19.00002d, 0d)
        };
        var navigationTarget = Quaternion.Euler(0f, 25f, 0f);
        var production = ReferenceFrameTransforms.PredictCameraFromBoardRotationOnly(
            visual, Quaternion.identity, navigationTarget);

        Assert.That(coordinator.TryInitializeFromVisualMeasurement(visual, 100L * millisecond, true, out var initializeReason), Is.True, initializeReason);
        var worldBoard = coordinator.Snapshot.worldFromBoard;
        var visualWorldCamera = coordinator.Snapshot.worldFromCamera;
        Assert.That(coordinator.TryBeginRotationPropagation(
            visual, 100L * millisecond, Quaternion.identity, positions, 300f, out var beginReason), Is.True, beginReason);
        Assert.That(coordinator.TryUpdateRotationPropagation(
            200L * millisecond, navigationTarget, production, positions, 300f, out var updateReason), Is.True, updateReason);
        Assert.That(coordinator.TryUpdateRotationPropagation(
            300L * millisecond, navigationTarget, production, positions, 300f, out var acceptedUpdateReason), Is.True, acceptedUpdateReason);

        var snapshot = coordinator.Snapshot;
        Assert.That(coordinator.ProductionHorizontalGnssApplied, Is.True);
        Assert.That(coordinator.ProductionHorizontalGnssState, Is.EqualTo(HorizontalGnssSmoothingState.FollowingFreshGnss));
        Assert.That(Mathf.Abs(snapshot.worldFromCamera.position.x - visualWorldCamera.position.x), Is.GreaterThan(Tolerance));
        Assert.That(Mathf.Abs(snapshot.worldFromCamera.position.y - visualWorldCamera.position.y), Is.GreaterThan(Tolerance));
        Assert.That(coordinator.LatestHorizontalGnssSmoothingShadow.navigationDelta.z, Is.Zero);
        Assert.That(Vector3.Distance(
            snapshot.worldFromCamera.position,
            visualWorldCamera.position + coordinator.LatestHorizontalGnssSmoothingShadow.worldDelta), Is.LessThan(Tolerance));
        Assert.That(Quaternion.Angle(snapshot.worldFromCamera.rotation, ReferenceFrameTransforms.Invert(production).rotation), Is.LessThanOrEqualTo(Tolerance));
        AssertPose(snapshot.worldFromBoard, worldBoard);
    }

    [TestCase(47.00001d, 19.00001d, 1f, 1f)]
    [TestCase(46.99999d, 18.99999d, -1f, -1f)]
    public void RotationPropagation_GnssNorthEastSignsMapIntoPersistentWorld(
        double targetLatitude,
        double targetLongitude,
        float expectedWorldXSign,
        float expectedWorldYSign)
    {
        const long millisecond = 1_000_000L;
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var visual = new Pose(new Vector3(0f, 0f, 1f), Quaternion.identity);
        var positions = new List<TelemetryAircraftPositionSample>
        {
            new(99L * millisecond, 47d, 19d, 0d),
            new(200L * millisecond, targetLatitude, targetLongitude, 0d),
            new(300L * millisecond, targetLatitude, targetLongitude, 0d)
        };

        Assert.That(coordinator.TryInitializeFromVisualMeasurement(visual, 100L * millisecond, true, out var initializeReason), Is.True, initializeReason);
        Assert.That(coordinator.TryBeginRotationPropagation(
            visual, 100L * millisecond, Quaternion.identity, positions, 300f, out var beginReason), Is.True, beginReason);
        Assert.That(coordinator.TryUpdateRotationPropagation(
            200L * millisecond, Quaternion.identity, visual, positions, 300f, out var updateReason), Is.True, updateReason);
        Assert.That(coordinator.TryUpdateRotationPropagation(
            300L * millisecond, Quaternion.identity, visual, positions, 300f, out var acceptedUpdateReason), Is.True, acceptedUpdateReason);

        var position = coordinator.Snapshot.worldFromCamera.position;
        Assert.That(position.x * expectedWorldXSign, Is.GreaterThan(Tolerance));
        Assert.That(position.y * expectedWorldYSign, Is.GreaterThan(Tolerance));
        Assert.That(position.z, Is.EqualTo(-1f).Within(Tolerance));
    }

    [Test]
    public void RotationPropagation_StaleGnssFreezesHorizontalEstimateWhileRotationContinuesThenResumesFresh()
    {
        const long millisecond = 1_000_000L;
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var visual = new Pose(new Vector3(0f, 0f, 1f), Quaternion.identity);
        var positions = new List<TelemetryAircraftPositionSample>
        {
            new(99L * millisecond, 47d, 19d, 0d),
            new(200L * millisecond, 47.00001d, 19d, 0d)
        };

        Assert.That(coordinator.TryInitializeFromVisualMeasurement(visual, 100L * millisecond, true, out var initializeReason), Is.True, initializeReason);
        Assert.That(coordinator.TryBeginRotationPropagation(
            visual, 100L * millisecond, Quaternion.identity, positions, 300f, out var beginReason), Is.True, beginReason);
        var firstProduction = ReferenceFrameTransforms.PredictCameraFromBoardRotationOnly(visual, Quaternion.identity, Quaternion.Euler(0f, 10f, 0f));
        Assert.That(coordinator.TryUpdateRotationPropagation(
            200L * millisecond, Quaternion.Euler(0f, 10f, 0f), firstProduction, positions, 300f, out var firstReason), Is.True, firstReason);
        var freshSnapshot = coordinator.Snapshot;

        var staleProduction = ReferenceFrameTransforms.PredictCameraFromBoardRotationOnly(visual, Quaternion.identity, Quaternion.Euler(0f, 35f, 0f));
        Assert.That(coordinator.TryUpdateRotationPropagation(
            600L * millisecond, Quaternion.Euler(0f, 35f, 0f), staleProduction, positions, 300f, out var staleReason), Is.True, staleReason);
        var staleSnapshot = coordinator.Snapshot;
        Assert.That(coordinator.ProductionHorizontalGnssApplied, Is.True);
        Assert.That(coordinator.ProductionHorizontalGnssState, Is.EqualTo(HorizontalGnssSmoothingState.DegradedStaleGnss));
        Assert.That(staleSnapshot.worldFromCamera.position.x, Is.EqualTo(freshSnapshot.worldFromCamera.position.x).Within(Tolerance));
        Assert.That(staleSnapshot.worldFromCamera.position.y, Is.EqualTo(freshSnapshot.worldFromCamera.position.y).Within(Tolerance));
        Assert.That(staleSnapshot.worldFromCamera.position.z, Is.EqualTo(freshSnapshot.worldFromCamera.position.z).Within(Tolerance));
        Assert.That(Quaternion.Angle(staleSnapshot.worldFromCamera.rotation, freshSnapshot.worldFromCamera.rotation), Is.GreaterThan(1f));

        positions.Add(new TelemetryAircraftPositionSample(650L * millisecond, 47.00002d, 19d, 0d));
        var resumedProduction = ReferenceFrameTransforms.PredictCameraFromBoardRotationOnly(visual, Quaternion.identity, Quaternion.Euler(0f, 45f, 0f));
        Assert.That(coordinator.TryUpdateRotationPropagation(
            700L * millisecond, Quaternion.Euler(0f, 45f, 0f), resumedProduction, positions, 300f, out var resumedReason), Is.True, resumedReason);
        Assert.That(coordinator.ProductionHorizontalGnssState, Is.EqualTo(HorizontalGnssSmoothingState.FollowingFreshGnss));
        Assert.That(coordinator.Snapshot.worldFromCamera.position.x, Is.GreaterThan(staleSnapshot.worldFromCamera.position.x));
    }

    [Test]
    public void RotationPropagation_WeakSatelliteQualityFreezesHorizontalWhileRotationAndVisualReacquisitionContinue()
    {
        const long millisecond = 1_000_000L;
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var visual = new Pose(new Vector3(0f, 0f, 1f), Quaternion.identity);
        var positions = new List<TelemetryAircraftPositionSample>
        {
            new(99L * millisecond, 47d, 19d, 0d),
            new(200L * millisecond, 47.00001d, 19d, 0d),
            new(300L * millisecond, 47.00002d, 19d, 0d),
            new(600L * millisecond, 47.00020d, 19d, 0d),
            new(900L * millisecond, 47.00030d, 19d, 0d)
        };

        // Three weak callbacks make quality explicitly untrusted before loss.
        coordinator.ObserveHorizontalGnssQuality(18, 100L * millisecond);
        coordinator.ObserveHorizontalGnssQuality(20, 200L * millisecond);
        coordinator.ObserveHorizontalGnssQuality(21, 300L * millisecond);
        Assert.That(coordinator.HorizontalGnssQualityAdmission.trusted, Is.False);

        Assert.That(coordinator.TryInitializeFromVisualMeasurement(visual, 100L * millisecond, true, out var initializeReason), Is.True, initializeReason);
        Assert.That(coordinator.TryBeginRotationPropagation(
            visual, 100L * millisecond, Quaternion.identity, positions, 300f, out var beginReason), Is.True, beginReason);
        var production = ReferenceFrameTransforms.PredictCameraFromBoardRotationOnly(
            visual, Quaternion.identity, Quaternion.Euler(0f, 20f, 0f));
        Assert.That(coordinator.TryUpdateRotationPropagation(
            600L * millisecond, Quaternion.Euler(0f, 20f, 0f), production, positions, 300f, out var updateReason), Is.True, updateReason);

        var frozen = coordinator.Snapshot;
        Assert.That(coordinator.ProductionHorizontalGnssApplied, Is.True);
        Assert.That(coordinator.ProductionHorizontalGnssState, Is.EqualTo(HorizontalGnssSmoothingState.DegradedGnssQuality));
        Assert.That(coordinator.ProductionHorizontalGnssReason, Is.EqualTo("SATELLITE_COUNT_BELOW_ENTER_THRESHOLD"));
        Assert.That(frozen.worldFromCamera.position, Is.EqualTo(ReferenceFrameTransforms.Invert(visual).position));
        Assert.That(Quaternion.Angle(frozen.worldFromCamera.rotation, ReferenceFrameTransforms.Invert(visual).rotation), Is.GreaterThan(1f));

        // Recovery does not reset the outage baseline or snap the estimate.
        coordinator.ObserveHorizontalGnssQuality(23, 700L * millisecond);
        coordinator.ObserveHorizontalGnssQuality(24, 800L * millisecond);
        coordinator.ObserveHorizontalGnssQuality(25, 900L * millisecond);
        var resumedProduction = ReferenceFrameTransforms.PredictCameraFromBoardRotationOnly(
            visual, Quaternion.identity, Quaternion.Euler(0f, 35f, 0f));
        Assert.That(coordinator.TryUpdateRotationPropagation(
            900L * millisecond, Quaternion.Euler(0f, 35f, 0f), resumedProduction, positions, 300f, out var resumeReason), Is.True, resumeReason);
        Assert.That(coordinator.ProductionHorizontalGnssState, Is.EqualTo(HorizontalGnssSmoothingState.FollowingFreshGnss));
        Assert.That(Vector3.Distance(coordinator.Snapshot.worldFromCamera.position, frozen.worldFromCamera.position), Is.LessThan(0.76f));

        var reacquired = new Pose(new Vector3(0.3f, -0.1f, 1.2f), Quaternion.Euler(2f, 8f, -1f));
        coordinator.ObserveHorizontalGnssQuality(18, 1_000L * millisecond);
        coordinator.ObserveHorizontalGnssQuality(18, 1_100L * millisecond);
        coordinator.ObserveHorizontalGnssQuality(18, 1_200L * millisecond);
        Assert.That(coordinator.TryUpdateFromVisualMeasurement(
            reacquired, 1_200L * millisecond, true, positions, 300f, out var reacquireReason), Is.True, reacquireReason);
        AssertPose(coordinator.Snapshot.worldFromCamera, ReferenceFrameTransforms.Invert(reacquired));
    }

    [Test]
    public void RotationPropagation_AuthoritativeVisualReacquisitionImmediatelyReplacesGnssEstimate()
    {
        const long millisecond = 1_000_000L;
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var visual = new Pose(new Vector3(0f, 0f, 1f), Quaternion.identity);
        var reacquired = new Pose(new Vector3(0.3f, -0.2f, 1.1f), Quaternion.Euler(2f, 15f, -4f));
        var positions = new List<TelemetryAircraftPositionSample>
        {
            new(99L * millisecond, 47d, 19d, 0d),
            new(200L * millisecond, 47.00001d, 19d, 0d)
        };

        Assert.That(coordinator.TryInitializeFromVisualMeasurement(visual, 100L * millisecond, true, out var initializeReason), Is.True, initializeReason);
        Assert.That(coordinator.TryBeginRotationPropagation(
            visual, 100L * millisecond, Quaternion.identity, positions, 300f, out var beginReason), Is.True, beginReason);
        Assert.That(coordinator.TryUpdateRotationPropagation(
            200L * millisecond, Quaternion.identity, visual, positions, 300f, out var updateReason), Is.True, updateReason);
        Assert.That(coordinator.ProductionHorizontalGnssApplied, Is.True);

        Assert.That(coordinator.TryUpdateFromVisualMeasurement(
            reacquired, 300L * millisecond, true, positions, 300f, out var reacquisitionReason), Is.True, reacquisitionReason);
        AssertPose(coordinator.Snapshot.worldFromCamera, ReferenceFrameTransforms.Invert(reacquired));
        Assert.That(coordinator.Snapshot.phase, Is.EqualTo(WorldLocalizationPhase.VisualLocalized));
        Assert.That(coordinator.ProductionHorizontalGnssApplied, Is.False);
        Assert.That(coordinator.ProductionHorizontalGnssState, Is.EqualTo(HorizontalGnssSmoothingState.Uninitialized));
    }

    [Test]
    public void RotationPropagation_LongLivedTelemetryKeepsWorldBoardAndVisualVerticalAcrossThirtySeconds()
    {
        const long millisecond = 1_000_000L;
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var visual = new Pose(new Vector3(0f, 0f, 1.5f), Quaternion.identity);
        var positions = new List<TelemetryAircraftPositionSample>
        {
            new(99L * millisecond, 47d, 19d, 0d),
            new(5_000L * millisecond, 47.00001d, 19d, 0d),
            new(15_000L * millisecond, 47.00002d, 19.00001d, 0d),
            new(30_000L * millisecond, 47.00003d, 19.00002d, 0d)
        };

        Assert.That(coordinator.TryInitializeFromVisualMeasurement(visual, 100L * millisecond, true, out var initializeReason), Is.True, initializeReason);
        var worldBoard = coordinator.Snapshot.worldFromBoard;
        var expectedVertical = coordinator.Snapshot.worldFromCamera.position.z;
        Assert.That(coordinator.TryBeginRotationPropagation(
            visual, 100L * millisecond, Quaternion.identity, positions, 300f, out var beginReason), Is.True, beginReason);

        foreach (var targetMilliseconds in new[] { 5_000L, 15_000L, 30_000L })
        {
            var navigation = Quaternion.Euler(0f, targetMilliseconds / 1_000f, 0f);
            var production = ReferenceFrameTransforms.PredictCameraFromBoardRotationOnly(visual, Quaternion.identity, navigation);
            Assert.That(coordinator.TryUpdateRotationPropagation(
                targetMilliseconds * millisecond, navigation, production, positions, 300f, out var updateReason), Is.True, updateReason);
            Assert.That(coordinator.Snapshot.phase, Is.EqualTo(WorldLocalizationPhase.Propagating));
            Assert.That(coordinator.ProductionHorizontalGnssApplied, Is.True);
            Assert.That(coordinator.Snapshot.worldFromCamera.position.z, Is.EqualTo(expectedVertical).Within(Tolerance));
            AssertPose(coordinator.Snapshot.worldFromBoard, worldBoard);
        }

        Assert.That(coordinator.Snapshot.worldFromCamera.position.x, Is.GreaterThan(0f));
        Assert.That(coordinator.Snapshot.worldFromCamera.position.y, Is.GreaterThan(0f));
    }

    [Test]
    public void RotationPropagation_CrossingTwoSecondsKeepsTheSameBaselineAndSmoothedCameraPose()
    {
        const long millisecond = 1_000_000L;
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var visual = new Pose(new Vector3(0f, 0f, 1.25f), Quaternion.Euler(2f, 7f, -3f));
        var positions = new List<TelemetryAircraftPositionSample>
        {
            new(99L * millisecond, 47d, 19d, 0d),
            new(2_000L * millisecond, 47.00001d, 19.00001d, 0d),
            new(2_001L * millisecond, 47.00001d, 19.00001d, 0d)
        };

        Assert.That(coordinator.TryInitializeFromVisualMeasurement(visual, 100L * millisecond, true, out var initializeReason), Is.True, initializeReason);
        var worldBoard = coordinator.Snapshot.worldFromBoard;
        Assert.That(coordinator.TryBeginRotationPropagation(
            visual, 100L * millisecond, Quaternion.identity, positions, 300f, out var beginReason), Is.True, beginReason);

        Assert.That(coordinator.TryUpdateRotationPropagation(
            2_000L * millisecond, Quaternion.identity, visual, positions, 300f, out var beforeReason), Is.True, beforeReason);
        var beforeTransition = coordinator.Snapshot.worldFromCamera;
        var beforeSmootherTimestamp = coordinator.LatestHorizontalGnssSmoothingShadow.estimate.targetTimestampNs;

        Assert.That(coordinator.TryUpdateRotationPropagation(
            2_001L * millisecond, Quaternion.identity, visual, positions, 300f, out var afterReason), Is.True, afterReason);
        var afterTransition = coordinator.Snapshot.worldFromCamera;

        Assert.That(Vector3.Distance(afterTransition.position, beforeTransition.position), Is.LessThanOrEqualTo(0.0026f));
        Assert.That(Quaternion.Angle(afterTransition.rotation, beforeTransition.rotation), Is.LessThanOrEqualTo(Tolerance));
        Assert.That(coordinator.LatestHorizontalGnssSmoothingShadow.estimate.targetTimestampNs, Is.EqualTo(2_001L * millisecond));
        Assert.That(coordinator.LatestHorizontalGnssSmoothingShadow.estimate.targetTimestampNs, Is.GreaterThan(beforeSmootherTimestamp));
        AssertPose(coordinator.Snapshot.worldFromBoard, worldBoard);
    }

    [Test]
    public void RotationPropagation_LongOutageVisualReacquisitionReplacesTelemetryPoseAndResetsSmoothing()
    {
        const long millisecond = 1_000_000L;
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var visual = new Pose(new Vector3(0f, 0f, 1f), Quaternion.identity);
        var reacquired = new Pose(new Vector3(0.45f, -0.2f, 1.1f), Quaternion.Euler(4f, 28f, -6f));
        var positions = new List<TelemetryAircraftPositionSample>
        {
            new(99L * millisecond, 47d, 19d, 0d),
            new(10_000L * millisecond, 47.00002d, 19.00001d, 0d),
            new(10_100L * millisecond, 47.00002d, 19.00001d, 0d)
        };

        Assert.That(coordinator.TryInitializeFromVisualMeasurement(visual, 100L * millisecond, true, out var initializeReason), Is.True, initializeReason);
        var worldBoard = coordinator.Snapshot.worldFromBoard;
        Assert.That(coordinator.TryBeginRotationPropagation(
            visual, 100L * millisecond, Quaternion.identity, positions, 300f, out var beginReason), Is.True, beginReason);
        var propagated = ReferenceFrameTransforms.PredictCameraFromBoardRotationOnly(
            visual, Quaternion.identity, Quaternion.Euler(0f, 32f, 0f));
        Assert.That(coordinator.TryUpdateRotationPropagation(
            10_000L * millisecond, Quaternion.Euler(0f, 32f, 0f), propagated, positions, 300f, out var propagationReason), Is.True, propagationReason);
        Assert.That(coordinator.ProductionHorizontalGnssApplied, Is.True);

        Assert.That(coordinator.TryUpdateFromVisualMeasurement(
            reacquired, 10_100L * millisecond, true, positions, 300f, out var reacquisitionReason), Is.True, reacquisitionReason);

        Assert.That(coordinator.Snapshot.phase, Is.EqualTo(WorldLocalizationPhase.VisualLocalized));
        AssertPose(coordinator.Snapshot.worldFromCamera, ReferenceFrameTransforms.Invert(reacquired));
        AssertPose(coordinator.Snapshot.worldFromBoard, worldBoard);
        Assert.That(coordinator.HasActiveRotationPropagation, Is.False);
        Assert.That(coordinator.ProductionHorizontalGnssApplied, Is.False);
        Assert.That(coordinator.ProductionHorizontalGnssState, Is.EqualTo(HorizontalGnssSmoothingState.Uninitialized));
    }

    private static void AssertWorldCompositionInvariant(
        WorldLocalizationSnapshot snapshot,
        Pose cameraFromBoard)
    {
        var recomposedWorldFromBoard = ReferenceFrameTransforms.Compose(
            ReferenceFrameTransforms.WorldFromCamera(snapshot.worldFromCamera),
            ReferenceFrameTransforms.MatrixFromPose(cameraFromBoard));
        var expectedWorldFromBoard = ReferenceFrameTransforms.WorldFromBoard(snapshot.worldFromBoard);
        for (var row = 0; row < 4; row++)
        for (var column = 0; column < 4; column++)
            Assert.That(recomposedWorldFromBoard[row, column], Is.EqualTo(expectedWorldFromBoard[row, column]).Within(Tolerance));
    }

    private static void AssertPose(Pose actual, Pose expected)
    {
        Assert.That(Vector3.Distance(actual.position, expected.position), Is.LessThanOrEqualTo(Tolerance));
        Assert.That(Quaternion.Angle(actual.rotation, expected.rotation), Is.LessThanOrEqualTo(Tolerance));
    }
}
