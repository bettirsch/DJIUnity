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
                        persistentReferenceFrame.HasReferencePose;
        Debug.Log($"PERSISTENT_REFERENCE_AVAILABLE={available}");
        if (!available)
        {
            Debug.LogWarning("[Persistent Reference] PERSISTENT_REFERENCE_UNAVAILABLE scene=DroneView");
            return;
        }

        var worldFromReference = persistentReferenceFrame.ReferenceWorldPose;
        Debug.Log($"REFERENCE_WORLD_POSITION={worldFromReference.position}");
        Debug.Log($"REFERENCE_WORLD_ROTATION={worldFromReference.rotation.eulerAngles}");
    }
}
