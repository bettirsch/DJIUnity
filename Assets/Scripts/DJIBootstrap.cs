using UnityEngine;
using System;

public class DJIBootstrap : MonoBehaviour
{
    void Start()
    {
        try
        {
            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            {
                var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                var app = activity.Call<AndroidJavaObject>("getApplication");
                using var plugin = new AndroidJavaClass("com.sok9hu.djibridge.DJIPlugin");
                plugin.CallStatic("init", app);
            }
            Debug.Log("DJI plugin initialized successfully.");
        }
        catch (Exception e)
        {
            Debug.LogError("DJI plugin init failed: " + e);
        }
    }
}
