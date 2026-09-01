using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Scene 2 only verifies that the phone-acquired T_world_reference survived the scene transition.
/// DJI localization deliberately does not start here.
/// </summary>
public sealed class PersistentReferenceFrameScene2Diagnostics : MonoBehaviour
{
    private const string DroneViewSceneName = "DroneView";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void CreateForDroneView()
    {
        if (SceneManager.GetActiveScene().name != DroneViewSceneName ||
            FindFirstObjectByType<PersistentReferenceFrameScene2Diagnostics>() != null)
        {
            return;
        }

        var diagnosticsObject = new GameObject("Scene 2 Reference Frame Diagnostics");
        diagnosticsObject.AddComponent<PersistentReferenceFrameScene2Diagnostics>();
    }

    private void Start()
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
        Debug.Log(
            $"[Persistent Reference] PERSISTENT_REFERENCE_AVAILABLE " +
            $"ReferenceWorldPosition={worldFromReference.position} " +
            $"ReferenceWorldRotation={worldFromReference.rotation.eulerAngles}");
    }
}
