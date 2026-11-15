using UnityEngine;

/// <summary>Build-only diagnostic capability. Arming still requires a one-shot developer token.</summary>
public static class DjiDetectorCaptureDiagnostics
{
    public static void Configure(string sessionId, bool enabled)
    {
#if DJI_EXACT_DETECTOR_CAPTURE && UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using var bridge = new AndroidJavaClass("com.sok9hu.djibridge.DjiBoardVisionBridge");
            bridge.CallStatic("configureDiagnosticCapture", sessionId ?? string.Empty, enabled);
        }
        catch (System.Exception exception)
        {
            Debug.LogWarning("DJI_EXACT_Y_CAPTURE_CONFIGURATION_FAILED " + exception.Message);
        }
#endif
    }
}
