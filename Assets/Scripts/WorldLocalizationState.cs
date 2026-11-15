using UnityEngine;

/// <summary>
/// Phases supported by the initial persistent-world localization contract.
/// Further propagation and relocalization phases deliberately belong to later
/// migration steps.
/// </summary>
public enum WorldLocalizationPhase
{
    Uninitialized,
    VisualLocalized,
    Propagating
}

/// <summary>
/// Immutable view of the persistent-world localization contract. Transform
/// names use T_A_B semantics: coordinates in B are mapped into A.
/// </summary>
public readonly struct WorldLocalizationSnapshot
{
    public readonly bool hasWorld;
    public readonly WorldLocalizationPhase phase;
    public readonly Pose worldFromBoard;
    public readonly Pose worldFromCamera;
    public readonly long lastVisualSourceTimestampNs;
    public readonly long lastPropagationTimestampNs;

    public WorldLocalizationSnapshot(
        bool hasWorld,
        WorldLocalizationPhase phase,
        Pose worldFromBoard,
        Pose worldFromCamera,
        long lastVisualSourceTimestampNs,
        long lastPropagationTimestampNs)
    {
        this.hasWorld = hasWorld;
        this.phase = phase;
        this.worldFromBoard = worldFromBoard;
        this.worldFromCamera = worldFromCamera;
        this.lastVisualSourceTimestampNs = lastVisualSourceTimestampNs;
        this.lastPropagationTimestampNs = lastPropagationTimestampNs;
    }
}

/// <summary>
/// Pure state core for the persistent DJI-world visual-localization shadow.
/// The first trusted visual T_camera_board measurement establishes a World
/// frame that coincides with the physical board frame. Later trusted visual
/// measurements update only T_world_camera; T_world_board stays fixed.
/// </summary>
public sealed class WorldLocalizationState
{
    private Pose _worldFromBoard = Pose.identity;
    private Pose _worldFromCamera = Pose.identity;
    private long _lastVisualSourceTimestampNs;
    private long _lastPropagationTimestampNs;

    public bool HasWorld { get; private set; }
    public WorldLocalizationPhase Phase { get; private set; } = WorldLocalizationPhase.Uninitialized;

    public WorldLocalizationSnapshot Snapshot => new(
        HasWorld,
        Phase,
        _worldFromBoard,
        _worldFromCamera,
        _lastVisualSourceTimestampNs,
        _lastPropagationTimestampNs);

    /// <summary>
    /// Establishes the first persistent World from a trusted visual pose.
    /// T_world_board starts as identity, therefore:
    /// T_world_camera = T_world_board * inverse(T_camera_board).
    /// This initial core intentionally cannot overwrite an established world.
    /// </summary>
    public bool InitializeFromVisual(
        Pose cameraFromBoard,
        long sourceTimestampNs,
        out string rejectionReason)
    {
        rejectionReason = string.Empty;
        if (HasWorld)
        {
            rejectionReason = "WORLD_ALREADY_INITIALIZED";
            return false;
        }

        if (sourceTimestampNs <= 0L)
        {
            rejectionReason = "INVALID_SOURCE_TIMESTAMP";
            return false;
        }

        if (!IsValidRigidPose(cameraFromBoard))
        {
            rejectionReason = "INVALID_CAMERA_FROM_BOARD";
            return false;
        }

        _worldFromBoard = Pose.identity;
        _worldFromCamera = CalculateWorldFromCamera(_worldFromBoard, cameraFromBoard);
        _lastVisualSourceTimestampNs = sourceTimestampNs;
        _lastPropagationTimestampNs = 0L;
        HasWorld = true;
        Phase = WorldLocalizationPhase.VisualLocalized;
        return true;
    }

    /// <summary>
    /// Updates the visual camera pose in the established World frame. The
    /// board is stationary by contract, so T_world_board is never rewritten.
    /// Source timestamps must be strictly increasing to prevent an older
    /// accepted frame from moving the shadow camera back in time.
    /// </summary>
    public bool UpdateFromVisual(
        Pose cameraFromBoard,
        long sourceTimestampNs,
        out string rejectionReason)
    {
        rejectionReason = string.Empty;
        if (!HasWorld)
        {
            rejectionReason = "WORLD_NOT_INITIALIZED";
            return false;
        }

        if (sourceTimestampNs <= _lastVisualSourceTimestampNs)
        {
            rejectionReason = "NON_INCREASING_SOURCE_TIMESTAMP";
            return false;
        }

        if (!IsValidRigidPose(cameraFromBoard))
        {
            rejectionReason = "INVALID_CAMERA_FROM_BOARD";
            return false;
        }

        _worldFromCamera = CalculateWorldFromCamera(_worldFromBoard, cameraFromBoard);
        _lastVisualSourceTimestampNs = sourceTimestampNs;
        _lastPropagationTimestampNs = 0L;
        Phase = WorldLocalizationPhase.VisualLocalized;
        return true;
    }

    /// <summary>
    /// Mirrors a production rotation-only prediction into the persistent World
    /// state. The caller owns prediction math; this core only records the
    /// resulting T_camera_board while keeping the last visual source intact.
    /// </summary>
    public bool UpdateFromRotationPropagation(
        Pose cameraFromBoard,
        long visualBaselineTimestampNs,
        long propagationTimestampNs,
        out string rejectionReason)
    {
        rejectionReason = string.Empty;
        if (!HasWorld)
        {
            rejectionReason = "WORLD_NOT_INITIALIZED";
            return false;
        }

        if (visualBaselineTimestampNs != _lastVisualSourceTimestampNs)
        {
            rejectionReason = "VISUAL_BASELINE_TIMESTAMP_MISMATCH";
            return false;
        }

        if (propagationTimestampNs <= visualBaselineTimestampNs ||
            propagationTimestampNs <= _lastPropagationTimestampNs)
        {
            rejectionReason = "NON_INCREASING_PROPAGATION_TIMESTAMP";
            return false;
        }

        if (!IsValidRigidPose(cameraFromBoard))
        {
            rejectionReason = "INVALID_CAMERA_FROM_BOARD";
            return false;
        }

        _worldFromCamera = CalculateWorldFromCamera(_worldFromBoard, cameraFromBoard);
        _lastPropagationTimestampNs = propagationTimestampNs;
        Phase = WorldLocalizationPhase.Propagating;
        return true;
    }

    /// <summary>
    /// Mirrors a production prediction selected from an exact, previously
    /// authoritative visual baseline. The coordinator owns the bounded
    /// baseline history; this core only verifies that the supplied historical
    /// pose belongs to the same fixed World board before accepting it.
    /// </summary>
    public bool UpdateFromResolvedVisualBaselineRotationPropagation(
        Pose cameraFromBoard,
        Pose visualBaselineCameraFromBoard,
        Pose worldFromCameraAtVisualBaseline,
        long visualBaselineTimestampNs,
        long propagationTimestampNs,
        out string rejectionReason)
    {
        rejectionReason = string.Empty;
        if (!HasWorld)
        {
            rejectionReason = "WORLD_NOT_INITIALIZED";
            return false;
        }

        if (visualBaselineTimestampNs <= 0L ||
            visualBaselineTimestampNs > _lastVisualSourceTimestampNs)
        {
            rejectionReason = "INVALID_RESOLVED_VISUAL_BASELINE_TIMESTAMP";
            return false;
        }

        if (propagationTimestampNs <= visualBaselineTimestampNs ||
            propagationTimestampNs <= _lastPropagationTimestampNs)
        {
            rejectionReason = "NON_INCREASING_PROPAGATION_TIMESTAMP";
            return false;
        }

        if (!IsValidRigidPose(cameraFromBoard) ||
            !IsValidRigidPose(visualBaselineCameraFromBoard) ||
            !IsValidRigidPose(worldFromCameraAtVisualBaseline))
        {
            rejectionReason = "INVALID_CAMERA_FROM_BOARD";
            return false;
        }

        var recomposedWorldFromBoard = ReferenceFrameTransforms.Compose(
            ReferenceFrameTransforms.WorldFromCamera(worldFromCameraAtVisualBaseline),
            ReferenceFrameTransforms.MatrixFromPose(visualBaselineCameraFromBoard));
        if (!MatricesApproximatelyEqual(
                recomposedWorldFromBoard,
                ReferenceFrameTransforms.WorldFromBoard(_worldFromBoard)))
        {
            rejectionReason = "RESOLVED_VISUAL_BASELINE_WORLD_INVARIANT_FAILED";
            return false;
        }

        _worldFromCamera = CalculateWorldFromCamera(_worldFromBoard, cameraFromBoard);
        _lastPropagationTimestampNs = propagationTimestampNs;
        Phase = WorldLocalizationPhase.Propagating;
        return true;
    }

    private static Pose CalculateWorldFromCamera(Pose worldFromBoard, Pose cameraFromBoard)
    {
        var normalizedCameraFromBoard = new Pose(
            cameraFromBoard.position,
            Quaternion.Normalize(cameraFromBoard.rotation));
        return ReferenceFrameTransforms.PoseFromMatrix(
            ReferenceFrameTransforms.Compose(
                ReferenceFrameTransforms.WorldFromBoard(worldFromBoard),
                ReferenceFrameTransforms.MatrixFromPose(
                    ReferenceFrameTransforms.Invert(normalizedCameraFromBoard))));
    }

    private static bool IsValidRigidPose(Pose pose)
    {
        if (!float.IsFinite(pose.position.x) ||
            !float.IsFinite(pose.position.y) ||
            !float.IsFinite(pose.position.z) ||
            !float.IsFinite(pose.rotation.x) ||
            !float.IsFinite(pose.rotation.y) ||
            !float.IsFinite(pose.rotation.z) ||
            !float.IsFinite(pose.rotation.w))
        {
            return false;
        }

        return pose.rotation.x * pose.rotation.x +
               pose.rotation.y * pose.rotation.y +
               pose.rotation.z * pose.rotation.z +
               pose.rotation.w * pose.rotation.w > 0.0000001f;
    }

    private static bool MatricesApproximatelyEqual(Matrix4x4 first, Matrix4x4 second)
    {
        const float tolerance = 0.0001f;
        for (var row = 0; row < 4; row++)
        for (var column = 0; column < 4; column++)
        {
            if (Mathf.Abs(first[row, column] - second[row, column]) > tolerance)
                return false;
        }

        return true;
    }
}
