using NUnit.Framework;
using UnityEngine;

public sealed class PersistentWorldVisualRenderPathTests
{
    private const float Tolerance = 0.0001f;
    private const long FirstTimestampNs = 1_000_000_000L;
    private const long CurrentTimestampNs = 2_000_000_000L;

    [Test]
    public void DefaultPath_IsPersistentWorldPath()
    {
        Assert.That(
            PersistentWorldVisualRenderPath.DefaultPath,
            Is.EqualTo(VisualRenderPath.PersistentWorldPath));
    }

    [Test]
    public void LegacyPath_PreservesTheCurrentRawVisualPose()
    {
        var rawVisualPose = CreatePose();
        var snapshot = CreateWorldSnapshot(rawVisualPose, CurrentTimestampNs);

        var resolution = PersistentWorldVisualRenderPath.Resolve(
            VisualRenderPath.LegacyBoardPath,
            rawVisualPose,
            CurrentTimestampNs,
            snapshot);

        AssertPose(resolution.rawCameraFromBoard, rawVisualPose);
        Assert.That(resolution.worldPathEligible, Is.True);
        Assert.That(resolution.worldPathUsed, Is.False);
        Assert.That(resolution.fallbackReason, Is.EqualTo("MODE_LEGACY"));
    }

    [Test]
    public void PersistentWorldPath_ReconstructsTheLegacyRawVisualPose()
    {
        var rawVisualPose = CreatePose();
        var snapshot = CreateWorldSnapshot(rawVisualPose, CurrentTimestampNs);

        var resolution = PersistentWorldVisualRenderPath.Resolve(
            VisualRenderPath.PersistentWorldPath,
            rawVisualPose,
            CurrentTimestampNs,
            snapshot);

        AssertPose(resolution.rawCameraFromBoard, rawVisualPose);
        Assert.That(resolution.worldPathEligible, Is.True);
        Assert.That(resolution.worldPathUsed, Is.True);
        Assert.That(resolution.fallbackReason, Is.EqualTo("NONE"));
    }

    [Test]
    public void TimestampMismatch_FallsBackToLegacyWithoutUsingStaleWorldPose()
    {
        var rawVisualPose = CreatePose();
        var snapshot = CreateWorldSnapshot(rawVisualPose, CurrentTimestampNs);

        var resolution = PersistentWorldVisualRenderPath.Resolve(
            VisualRenderPath.PersistentWorldPath,
            rawVisualPose,
            CurrentTimestampNs + 1L,
            snapshot);

        AssertPose(resolution.rawCameraFromBoard, rawVisualPose);
        Assert.That(resolution.worldPathEligible, Is.False);
        Assert.That(resolution.worldPathUsed, Is.False);
        Assert.That(resolution.fallbackReason, Is.EqualTo("WORLD_TIMESTAMP_MISMATCH"));
    }

    [Test]
    public void MissingWorld_FallsBackToLegacy()
    {
        var rawVisualPose = CreatePose();

        var resolution = PersistentWorldVisualRenderPath.Resolve(
            VisualRenderPath.PersistentWorldPath,
            rawVisualPose,
            CurrentTimestampNs,
            default);

        AssertPose(resolution.rawCameraFromBoard, rawVisualPose);
        Assert.That(resolution.worldPathEligible, Is.False);
        Assert.That(resolution.worldPathUsed, Is.False);
        Assert.That(resolution.fallbackReason, Is.EqualTo("WORLD_UNINITIALIZED"));
    }

    [Test]
    public void TemporalCompensationInputs_AreEquivalentForLegacyAndWorldPaths()
    {
        var previous = new Pose(new Vector3(-0.2f, 0.1f, 1.2f), Quaternion.Euler(2f, 8f, -3f));
        var current = new Pose(new Vector3(0.3f, -0.4f, 1.5f), Quaternion.Euler(6f, 19f, 4f));
        var state = new WorldLocalizationState();
        Assert.That(state.InitializeFromVisual(previous, FirstTimestampNs, out var initializeReason), Is.True, initializeReason);
        Assert.That(state.UpdateFromVisual(current, CurrentTimestampNs, out var updateReason), Is.True, updateReason);

        var legacy = PersistentWorldVisualRenderPath.Resolve(
            VisualRenderPath.LegacyBoardPath,
            current,
            CurrentTimestampNs,
            state.Snapshot);
        var world = PersistentWorldVisualRenderPath.Resolve(
            VisualRenderPath.PersistentWorldPath,
            current,
            CurrentTimestampNs,
            state.Snapshot);

        // TEMP OFF receives equivalent raw poses directly.
        AssertPose(legacy.rawCameraFromBoard, world.rawCameraFromBoard);

        // TEMP ON consumes those same raw inputs through unchanged predictors.
        var legacyPredicted = Predict(previous, legacy.rawCameraFromBoard);
        var worldPredicted = Predict(previous, world.rawCameraFromBoard);
        AssertPose(legacyPredicted, worldPredicted);
    }

    [Test]
    public void LegacyWorldLegacySelection_DoesNotMutateWorldLocalizationState()
    {
        var rawVisualPose = CreatePose();
        var state = new WorldLocalizationState();
        Assert.That(state.InitializeFromVisual(rawVisualPose, CurrentTimestampNs, out var reason), Is.True, reason);
        var before = state.Snapshot;

        PersistentWorldVisualRenderPath.Resolve(
            VisualRenderPath.LegacyBoardPath,
            rawVisualPose,
            CurrentTimestampNs,
            state.Snapshot);
        PersistentWorldVisualRenderPath.Resolve(
            VisualRenderPath.PersistentWorldPath,
            rawVisualPose,
            CurrentTimestampNs,
            state.Snapshot);
        PersistentWorldVisualRenderPath.Resolve(
            VisualRenderPath.LegacyBoardPath,
            rawVisualPose,
            CurrentTimestampNs,
            state.Snapshot);

        var after = state.Snapshot;
        Assert.That(after.hasWorld, Is.EqualTo(before.hasWorld));
        Assert.That(after.phase, Is.EqualTo(before.phase));
        Assert.That(after.lastVisualSourceTimestampNs, Is.EqualTo(before.lastVisualSourceTimestampNs));
        AssertPose(after.worldFromBoard, before.worldFromBoard);
        AssertPose(after.worldFromCamera, before.worldFromCamera);
    }

    private static WorldLocalizationSnapshot CreateWorldSnapshot(Pose rawVisualPose, long timestampNs)
    {
        var state = new WorldLocalizationState();
        Assert.That(state.InitializeFromVisual(rawVisualPose, timestampNs, out var reason), Is.True, reason);
        return state.Snapshot;
    }

    private static Pose Predict(Pose previous, Pose current)
    {
        var predictor = new VisualTemporalCompensationPredictor();
        predictor.AddAcceptedVisualPose(1L, FirstTimestampNs, previous);
        predictor.AddAcceptedVisualPose(2L, CurrentTimestampNs, current);
        Assert.That(
            predictor.TryPredict(
                2L,
                CurrentTimestampNs + 50_000_000L,
                CurrentTimestampNs + 50_000_000L,
                new VisualTemporalCompensationConfiguration
                {
                    maxVisualSampleIntervalMilliseconds = 1_500f
                },
                out var predicted,
                out _,
                out _,
                out _,
                out var predictionReason),
            Is.True,
            predictionReason);
        return predicted;
    }

    private static Pose CreatePose() => new(
        new Vector3(0.4f, -0.2f, 1.3f),
        Quaternion.Euler(13f, -27f, 8f));

    private static void AssertPose(Pose actual, Pose expected)
    {
        Assert.That(Vector3.Distance(actual.position, expected.position), Is.LessThanOrEqualTo(Tolerance));
        Assert.That(Quaternion.Angle(actual.rotation, expected.rotation), Is.LessThanOrEqualTo(Tolerance));
    }
}
