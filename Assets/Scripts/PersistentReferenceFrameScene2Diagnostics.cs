using UnityEngine;
/// <summary>
/// Scene 2 only verifies that the phone-acquired T_world_reference survived the scene transition.
/// DJI localization deliberately does not start here.
/// </summary>
public static class PersistentReferenceFrameScene2Diagnostics
{
    public static void LogForDroneView()
    {
        var available = PersistentReferenceFrame.TryGetExisting(out var persistentReferenceFrame) &&
                        persistentReferenceFrame.HasBoardPose;
        Debug.Log($"PERSISTENT_REFERENCE_AVAILABLE={available}");
        if (!available)
        {
            Debug.LogWarning("[Persistent Reference] PERSISTENT_REFERENCE_UNAVAILABLE scene=DroneView");
            return;
        }

        var worldFromBoard = persistentReferenceFrame.BoardWorldPose;
        Debug.Log($"T_WORLD_BOARD position={worldFromBoard.position} rotation={worldFromBoard.rotation.eulerAngles}");
        Debug.Log($"REFERENCE_WORLD_POSITION={worldFromBoard.position}");
        Debug.Log($"REFERENCE_WORLD_ROTATION={worldFromBoard.rotation.eulerAngles}");
    }
}
