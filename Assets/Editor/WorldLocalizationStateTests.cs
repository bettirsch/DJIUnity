using NUnit.Framework;
using UnityEngine;

public sealed class WorldLocalizationStateTests
{
    private const float Tolerance = 0.0001f;

    [Test]
    public void InitializeFromVisual_IdentityCameraFromBoardCreatesIdentityWorldCamera()
    {
        var state = new WorldLocalizationState();

        var initialized = state.InitializeFromVisual(Pose.identity, 1L, out var reason);

        Assert.That(initialized, Is.True, reason);
        Assert.That(state.HasWorld, Is.True);
        Assert.That(state.Phase, Is.EqualTo(WorldLocalizationPhase.VisualLocalized));
        AssertPose(state.Snapshot.worldFromBoard, Pose.identity);
        AssertPose(state.Snapshot.worldFromCamera, Pose.identity);
    }

    [Test]
    public void InitializeFromVisual_InvertsCameraFromBoardTranslationOnAllAxes()
    {
        AssertInverseTranslation(Vector3.right);
        AssertInverseTranslation(Vector3.up * 2f);
        AssertInverseTranslation(Vector3.forward * 3f);
    }

    [TestCase(0f, 37f, 0f)]
    [TestCase(19f, 0f, 0f)]
    [TestCase(0f, 0f, -43f)]
    public void InitializeFromVisual_YawPitchAndRollPreserveWorldCompositionInvariant(
        float pitchDegrees,
        float yawDegrees,
        float rollDegrees)
    {
        var cameraFromBoard = new Pose(
            new Vector3(0.2f, -0.3f, 1.4f),
            Quaternion.Euler(pitchDegrees, yawDegrees, rollDegrees));
        var state = new WorldLocalizationState();

        Assert.That(state.InitializeFromVisual(cameraFromBoard, 7L, out var reason), Is.True, reason);

        AssertWorldCompositionInvariant(state.Snapshot, cameraFromBoard);
    }

    [Test]
    public void InitializeFromVisual_CombinedTransformUsesBoardToCameraInverse()
    {
        var cameraFromBoard = new Pose(
            new Vector3(-1.1f, 0.45f, 2.3f),
            Quaternion.Euler(23f, -51f, 12f));
        var state = new WorldLocalizationState();

        Assert.That(state.InitializeFromVisual(cameraFromBoard, 123456789L, out var reason), Is.True, reason);
        AssertPose(
            state.Snapshot.worldFromCamera,
            ReferenceFrameTransforms.Invert(cameraFromBoard));
        AssertWorldCompositionInvariant(state.Snapshot, cameraFromBoard);
    }

    [Test]
    public void Snapshot_IsReadOnlyAndDoesNotMutatePersistentState()
    {
        var state = new WorldLocalizationState();
        var cameraFromBoard = new Pose(new Vector3(0.2f, 0.4f, 1f), Quaternion.Euler(8f, 16f, 24f));
        Assert.That(state.InitializeFromVisual(cameraFromBoard, 99L, out var reason), Is.True, reason);

        var firstSnapshot = state.Snapshot;
        var secondSnapshot = state.Snapshot;

        AssertPose(firstSnapshot.worldFromBoard, secondSnapshot.worldFromBoard);
        AssertPose(firstSnapshot.worldFromCamera, secondSnapshot.worldFromCamera);
        Assert.That(firstSnapshot.lastVisualSourceTimestampNs, Is.EqualTo(secondSnapshot.lastVisualSourceTimestampNs));
        Assert.That(state.InitializeFromVisual(Pose.identity, 100L, out var secondReason), Is.False);
        Assert.That(secondReason, Is.EqualTo("WORLD_ALREADY_INITIALIZED"));
        AssertPose(state.Snapshot.worldFromCamera, firstSnapshot.worldFromCamera);
    }

    [Test]
    public void InitializeFromVisual_RejectsInvalidTimestampAndPoseWithoutChangingPhase()
    {
        var invalidTimestampState = new WorldLocalizationState();
        Assert.That(invalidTimestampState.InitializeFromVisual(Pose.identity, 0L, out var timestampReason), Is.False);
        Assert.That(timestampReason, Is.EqualTo("INVALID_SOURCE_TIMESTAMP"));
        Assert.That(invalidTimestampState.HasWorld, Is.False);
        Assert.That(invalidTimestampState.Phase, Is.EqualTo(WorldLocalizationPhase.Uninitialized));

        var invalidPoseState = new WorldLocalizationState();
        var invalidPose = new Pose(Vector3.zero, new Quaternion(0f, 0f, 0f, 0f));
        Assert.That(invalidPoseState.InitializeFromVisual(invalidPose, 1L, out var poseReason), Is.False);
        Assert.That(poseReason, Is.EqualTo("INVALID_CAMERA_FROM_BOARD"));
        Assert.That(invalidPoseState.HasWorld, Is.False);
        Assert.That(invalidPoseState.Phase, Is.EqualTo(WorldLocalizationPhase.Uninitialized));
    }

    [TestCase(1f, 0f, 0f)]
    [TestCase(-1f, 0f, 0f)]
    [TestCase(0f, 1f, 0f)]
    [TestCase(0f, -1f, 0f)]
    [TestCase(0f, 0f, 1f)]
    [TestCase(0f, 0f, -1f)]
    public void UpdateFromVisual_UpdatesCameraForMotionInEveryDirection(float x, float y, float z)
    {
        var state = new WorldLocalizationState();
        Assert.That(state.InitializeFromVisual(Pose.identity, 10L, out var initializeReason), Is.True, initializeReason);
        var originalWorldFromBoard = state.Snapshot.worldFromBoard;
        var cameraFromBoard = new Pose(new Vector3(x, y, z), Quaternion.identity);

        Assert.That(state.UpdateFromVisual(cameraFromBoard, 20L, out var updateReason), Is.True, updateReason);

        AssertPose(state.Snapshot.worldFromBoard, originalWorldFromBoard);
        AssertPose(state.Snapshot.worldFromCamera, ReferenceFrameTransforms.Invert(cameraFromBoard));
        Assert.That(state.Snapshot.lastVisualSourceTimestampNs, Is.EqualTo(20L));
        Assert.That(state.Phase, Is.EqualTo(WorldLocalizationPhase.VisualLocalized));
        AssertWorldCompositionInvariant(state.Snapshot, cameraFromBoard);
    }

    [TestCase(0f, 45f, 0f)]
    [TestCase(25f, 0f, 0f)]
    [TestCase(0f, 0f, -35f)]
    [TestCase(15f, -40f, 20f)]
    public void UpdateFromVisual_UpdatesCameraForYawPitchRollAndCombinedRotation(
        float pitchDegrees,
        float yawDegrees,
        float rollDegrees)
    {
        var state = new WorldLocalizationState();
        var initial = new Pose(new Vector3(0.1f, -0.2f, 0.3f), Quaternion.Euler(2f, 3f, 4f));
        var updated = new Pose(
            new Vector3(-0.5f, 0.6f, 1.2f),
            Quaternion.Euler(pitchDegrees, yawDegrees, rollDegrees));
        Assert.That(state.InitializeFromVisual(initial, 100L, out var initializeReason), Is.True, initializeReason);
        var originalWorldFromBoard = state.Snapshot.worldFromBoard;

        Assert.That(state.UpdateFromVisual(updated, 200L, out var updateReason), Is.True, updateReason);

        AssertPose(state.Snapshot.worldFromBoard, originalWorldFromBoard);
        AssertWorldCompositionInvariant(state.Snapshot, updated);
    }

    [Test]
    public void UpdateFromVisual_RejectsEqualAndOlderTimestampsWithoutMutatingState()
    {
        var state = new WorldLocalizationState();
        var initial = new Pose(new Vector3(0.1f, 0.2f, 0.3f), Quaternion.identity);
        var newer = new Pose(new Vector3(0.4f, 0.5f, 0.6f), Quaternion.Euler(5f, 10f, 15f));
        Assert.That(state.InitializeFromVisual(initial, 100L, out var initializeReason), Is.True, initializeReason);
        Assert.That(state.UpdateFromVisual(newer, 200L, out var updateReason), Is.True, updateReason);
        var acceptedSnapshot = state.Snapshot;

        Assert.That(state.UpdateFromVisual(Pose.identity, 200L, out var equalReason), Is.False);
        Assert.That(equalReason, Is.EqualTo("NON_INCREASING_SOURCE_TIMESTAMP"));
        Assert.That(state.UpdateFromVisual(Pose.identity, 199L, out var olderReason), Is.False);
        Assert.That(olderReason, Is.EqualTo("NON_INCREASING_SOURCE_TIMESTAMP"));
        AssertPose(state.Snapshot.worldFromBoard, acceptedSnapshot.worldFromBoard);
        AssertPose(state.Snapshot.worldFromCamera, acceptedSnapshot.worldFromCamera);
        Assert.That(state.Snapshot.lastVisualSourceTimestampNs, Is.EqualTo(acceptedSnapshot.lastVisualSourceTimestampNs));
    }

    [Test]
    public void UpdateFromVisual_RequiresInitializedWorld()
    {
        var state = new WorldLocalizationState();

        Assert.That(state.UpdateFromVisual(Pose.identity, 1L, out var reason), Is.False);
        Assert.That(reason, Is.EqualTo("WORLD_NOT_INITIALIZED"));
        Assert.That(state.HasWorld, Is.False);
        Assert.That(state.Phase, Is.EqualTo(WorldLocalizationPhase.Uninitialized));
    }

    [Test]
    public void RotationPropagation_HoldsTranslationAndPreservesVisualBaselineTimestamp()
    {
        var state = new WorldLocalizationState();
        var visual = new Pose(new Vector3(0.4f, -0.3f, 1.2f), Quaternion.Euler(4f, 8f, 12f));
        Assert.That(state.InitializeFromVisual(visual, 100L, out var initializeReason), Is.True, initializeReason);
        var originalBoard = state.Snapshot.worldFromBoard;
        var propagated = new Pose(visual.position, Quaternion.Euler(11f, -42f, 19f));

        Assert.That(state.UpdateFromRotationPropagation(propagated, 100L, 200L, out var reason), Is.True, reason);

        var snapshot = state.Snapshot;
        Assert.That(snapshot.phase, Is.EqualTo(WorldLocalizationPhase.Propagating));
        Assert.That(snapshot.lastVisualSourceTimestampNs, Is.EqualTo(100L));
        Assert.That(snapshot.lastPropagationTimestampNs, Is.EqualTo(200L));
        AssertPose(snapshot.worldFromBoard, originalBoard);
        Assert.That(Vector3.Distance(ReferenceFrameTransforms.Invert(snapshot.worldFromCamera).position, propagated.position),
            Is.LessThanOrEqualTo(Tolerance));
        AssertWorldCompositionInvariant(snapshot, propagated);
    }

    [Test]
    public void RotationPropagation_RejectsMismatchedOrNonIncreasingTimestampsWithoutMutation()
    {
        var state = new WorldLocalizationState();
        Assert.That(state.InitializeFromVisual(Pose.identity, 100L, out var initializeReason), Is.True, initializeReason);
        Assert.That(state.UpdateFromRotationPropagation(Pose.identity, 101L, 200L, out var mismatchReason), Is.False);
        Assert.That(mismatchReason, Is.EqualTo("VISUAL_BASELINE_TIMESTAMP_MISMATCH"));
        Assert.That(state.UpdateFromRotationPropagation(Pose.identity, 100L, 100L, out var staleReason), Is.False);
        Assert.That(staleReason, Is.EqualTo("NON_INCREASING_PROPAGATION_TIMESTAMP"));
        Assert.That(state.Snapshot.phase, Is.EqualTo(WorldLocalizationPhase.VisualLocalized));
        Assert.That(state.Snapshot.lastPropagationTimestampNs, Is.EqualTo(0L));
    }

    [Test]
    public void VisualReacquisition_AfterPropagationReturnsToVisualLocalized()
    {
        var state = new WorldLocalizationState();
        Assert.That(state.InitializeFromVisual(Pose.identity, 100L, out var initializeReason), Is.True, initializeReason);
        var propagated = new Pose(Vector3.zero, Quaternion.Euler(0f, 35f, 0f));
        Assert.That(state.UpdateFromRotationPropagation(propagated, 100L, 200L, out var propagationReason), Is.True, propagationReason);
        var visual = new Pose(new Vector3(0.1f, 0.2f, 0.3f), Quaternion.Euler(2f, 40f, 4f));

        Assert.That(state.UpdateFromVisual(visual, 300L, out var visualReason), Is.True, visualReason);

        Assert.That(state.Snapshot.phase, Is.EqualTo(WorldLocalizationPhase.VisualLocalized));
        Assert.That(state.Snapshot.lastPropagationTimestampNs, Is.EqualTo(0L));
        AssertWorldCompositionInvariant(state.Snapshot, visual);
    }

    private static void AssertInverseTranslation(Vector3 cameraFromBoardTranslation)
    {
        var state = new WorldLocalizationState();
        var cameraFromBoard = new Pose(cameraFromBoardTranslation, Quaternion.identity);

        Assert.That(state.InitializeFromVisual(cameraFromBoard, 1L, out var reason), Is.True, reason);
        Assert.That(
            Vector3.Distance(state.Snapshot.worldFromCamera.position, -cameraFromBoardTranslation),
            Is.LessThanOrEqualTo(Tolerance));
        AssertWorldCompositionInvariant(state.Snapshot, cameraFromBoard);
    }

    private static void AssertWorldCompositionInvariant(
        WorldLocalizationSnapshot snapshot,
        Pose cameraFromBoard)
    {
        var recomposedWorldFromBoard = ReferenceFrameTransforms.Compose(
            ReferenceFrameTransforms.WorldFromCamera(snapshot.worldFromCamera),
            ReferenceFrameTransforms.MatrixFromPose(cameraFromBoard));
        AssertMatrix(recomposedWorldFromBoard, ReferenceFrameTransforms.WorldFromBoard(snapshot.worldFromBoard));
    }

    private static void AssertPose(Pose actual, Pose expected)
    {
        Assert.That(Vector3.Distance(actual.position, expected.position), Is.LessThanOrEqualTo(Tolerance));
        Assert.That(Quaternion.Angle(actual.rotation, expected.rotation), Is.LessThanOrEqualTo(Tolerance));
    }

    private static void AssertMatrix(Matrix4x4 actual, Matrix4x4 expected)
    {
        for (var row = 0; row < 4; row++)
        for (var column = 0; column < 4; column++)
            Assert.That(actual[row, column], Is.EqualTo(expected[row, column]).Within(Tolerance));
    }
}
