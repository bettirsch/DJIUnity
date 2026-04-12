using UnityEngine;

public class DJIBootstrap : MonoBehaviour
{
    void Start()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        // Kept only as a scene/bootstrap marker. DJI startup now happens earlier from the
        // Android side via DJIBridge's startup provider, so we avoid a redundant second init here.
        Debug.Log("DJI bootstrap active; Android-side startup is handled by DJIBridge.");
#endif
    }
}
