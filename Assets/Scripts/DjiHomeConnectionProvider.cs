using System;
using UnityEngine;

/// <summary>
/// Reads the one DJI bridge connection lifecycle used by HomeScene. It never
/// starts video or creates a second DJI SDK listener.
/// </summary>
public static class DjiHomeConnectionProvider
{
#if UNITY_ANDROID && !UNITY_EDITOR
    private const string BridgeClassName = "com.sok9hu.djibridge.DJIPlugin";
#endif

    [Serializable]
    public struct Status
    {
        public bool expectedUsbDeviceAttached;
        public bool usbPermissionGranted;
        public bool sdkRegistered;
        public bool productConnected;
    }

    public static Status GetStatus()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using var bridge = new AndroidJavaClass(BridgeClassName);
            var json = bridge.CallStatic<string>("getHomeConnectionStatusJson");
            return string.IsNullOrEmpty(json) ? default : JsonUtility.FromJson<Status>(json);
        }
        catch (AndroidJavaException exception)
        {
            Debug.LogWarning($"HOME_DJI_CONNECTION_STATUS_UNAVAILABLE error={exception.Message}");
            return default;
        }
#else
        return default;
#endif
    }
}
