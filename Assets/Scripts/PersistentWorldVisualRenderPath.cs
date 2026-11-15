using UnityEngine;

/// <summary>
/// Selects the raw VISUAL pose route used by the DJI overlay. This is
/// deliberately independent from render authority: propagation and NONE keep
/// using their existing production paths.
/// </summary>
public enum VisualRenderPath
{
    LegacyBoardPath,
    PersistentWorldPath
}

/// <summary>
/// Immutable result of selecting a raw VISUAL camera-from-board pose.
/// </summary>
public readonly struct VisualRenderPathResolution
{
    public readonly Pose rawCameraFromBoard;
    public readonly bool worldPathEligible;
    public readonly bool worldPathUsed;
    public readonly string fallbackReason;

    public VisualRenderPathResolution(
        Pose rawCameraFromBoard,
        bool worldPathEligible,
        bool worldPathUsed,
        string fallbackReason)
    {
        this.rawCameraFromBoard = rawCameraFromBoard;
        this.worldPathEligible = worldPathEligible;
        this.worldPathUsed = worldPathUsed;
        this.fallbackReason = fallbackReason;
    }
}

/// <summary>
/// Read-only render-routing telemetry for the currently applied VISUAL pose.
/// It is recorded with the existing visual-latency stream only.
/// </summary>
public readonly struct VisualRenderPathDiagnostics
{
    public readonly VisualRenderPath selectedPath;
    public readonly int segmentId;
    public readonly long sourceTimestampNs;
    public readonly bool worldPathEligible;
    public readonly bool worldPathUsed;
    public readonly string fallbackReason;
    public readonly int fallbackCount;

    public VisualRenderPathDiagnostics(
        VisualRenderPath selectedPath,
        int segmentId,
        long sourceTimestampNs,
        bool worldPathEligible,
        bool worldPathUsed,
        string fallbackReason,
        int fallbackCount)
    {
        this.selectedPath = selectedPath;
        this.segmentId = segmentId;
        this.sourceTimestampNs = sourceTimestampNs;
        this.worldPathEligible = worldPathEligible;
        this.worldPathUsed = worldPathUsed;
        this.fallbackReason = fallbackReason;
        this.fallbackCount = fallbackCount;
    }
}

/// <summary>
/// Reconstructs T_camera_board from the persistent-world contract. All matrix
/// products use the project's T_parent_child convention.
/// </summary>
public static class WorldLocalizationPoseResolver
{
    public static bool TryGetCameraFromBoard(
        WorldLocalizationSnapshot worldSnapshot,
        out Pose cameraFromBoard)
    {
        cameraFromBoard = Pose.identity;
        if (!worldSnapshot.hasWorld)
            return false;

        var cameraFromBoardMatrix = ReferenceFrameTransforms.CameraFromBoard(
            ReferenceFrameTransforms.CameraFromWorld(worldSnapshot.worldFromCamera),
            ReferenceFrameTransforms.WorldFromBoard(worldSnapshot.worldFromBoard));
        var resolvedPose = ReferenceFrameTransforms.PoseFromMatrix(cameraFromBoardMatrix);
        if (!IsFiniteRigidPose(resolvedPose))
            return false;

        cameraFromBoard = resolvedPose;
        return true;
    }

    private static bool IsFiniteRigidPose(Pose pose) =>
        float.IsFinite(pose.position.x) &&
        float.IsFinite(pose.position.y) &&
        float.IsFinite(pose.position.z) &&
        float.IsFinite(pose.rotation.x) &&
        float.IsFinite(pose.rotation.y) &&
        float.IsFinite(pose.rotation.z) &&
        float.IsFinite(pose.rotation.w) &&
        pose.rotation.x * pose.rotation.x +
        pose.rotation.y * pose.rotation.y +
        pose.rotation.z * pose.rotation.z +
        pose.rotation.w * pose.rotation.w > 0.0000001f;
}

/// <summary>
/// Pure A/B selector. It never mutates world localization, temporal
/// prediction, content, or render authority.
/// </summary>
public static class PersistentWorldVisualRenderPath
{
    // Step 6 promotes the physically validated VISUAL route. Legacy remains
    // selectable at runtime and is still the safe fallback for unavailable
    // world snapshots.
    public const VisualRenderPath DefaultPath = VisualRenderPath.PersistentWorldPath;

    public static VisualRenderPathResolution Resolve(
        VisualRenderPath selectedPath,
        Pose legacyRawCameraFromBoard,
        long sourceTimestampNs,
        WorldLocalizationSnapshot worldSnapshot)
    {
        var eligible = TryResolveWorldPose(
            sourceTimestampNs,
            worldSnapshot,
            out var worldRawCameraFromBoard,
            out var reason);
        if (selectedPath != VisualRenderPath.PersistentWorldPath)
        {
            return new VisualRenderPathResolution(
                legacyRawCameraFromBoard,
                eligible,
                false,
                "MODE_LEGACY");
        }

        return eligible
            ? new VisualRenderPathResolution(worldRawCameraFromBoard, true, true, "NONE")
            : new VisualRenderPathResolution(legacyRawCameraFromBoard, false, false, reason);
    }

    private static bool TryResolveWorldPose(
        long sourceTimestampNs,
        WorldLocalizationSnapshot worldSnapshot,
        out Pose worldRawCameraFromBoard,
        out string reason)
    {
        worldRawCameraFromBoard = Pose.identity;
        if (sourceTimestampNs <= 0L)
        {
            reason = "INVALID_SOURCE_TIMESTAMP";
            return false;
        }

        if (!worldSnapshot.hasWorld)
        {
            reason = "WORLD_UNINITIALIZED";
            return false;
        }

        if (worldSnapshot.lastVisualSourceTimestampNs != sourceTimestampNs)
        {
            reason = "WORLD_TIMESTAMP_MISMATCH";
            return false;
        }

        if (!WorldLocalizationPoseResolver.TryGetCameraFromBoard(worldSnapshot, out worldRawCameraFromBoard))
        {
            reason = "WORLD_POSE_INVALID";
            return false;
        }

        reason = "NONE";
        return true;
    }
}
