using UnityEngine;

public static class DJIPoseProvider
{
#if UNITY_ANDROID && !UNITY_EDITOR
    private const string BridgeClassName = "com.sok9hu.djibridge.DJIPoseBridge";
#endif

    public static string GetLatestPoseJson()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using var bridge = new AndroidJavaClass(BridgeClassName);
            return bridge.CallStatic<string>("getLatestPoseJson");
        }
        catch (AndroidJavaException e)
        {
            Debug.LogWarning("[DJI] Failed to fetch pose JSON: " + e.Message);
            return string.Empty;
        }
#else
        return string.Empty;
#endif
    }

    public static bool IsPoseAvailable()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using var bridge = new AndroidJavaClass(BridgeClassName);
            return bridge.CallStatic<bool>("isPoseAvailable");
        }
        catch (AndroidJavaException e)
        {
            Debug.LogWarning("[DJI] Failed to fetch pose availability: " + e.Message);
            return false;
        }
#else
        return false;
#endif
    }
}
