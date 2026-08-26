using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

internal static class DJIDroneViewBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        if (SceneManager.GetActiveScene().name != "DroneView")
            return;

        foreach (var canvas in Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            canvas.gameObject.SetActive(false);

        var cameraObject = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener), typeof(UniversalAdditionalCameraData));
        cameraObject.tag = "MainCamera";
        var camera = cameraObject.GetComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;

        var background = cameraObject.AddComponent<DJIGPUBackground>();
        var backgroundShader = Shader.Find("DJI/OESBackgroundURP");
        if (backgroundShader != null)
            background.backgroundMat = new Material(backgroundShader);

        background.verboseLogs = false;
        cameraObject.AddComponent<DJIAprilTagMarkerMvpController>();
    }
}
