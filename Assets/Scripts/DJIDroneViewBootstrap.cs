using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

internal static class DJIDroneViewBootstrap
{
    internal const string DroneViewSceneName = "DroneView";
    private const string DroneCameraName = "DJI Drone View Camera";
    private const string RuntimeDiagnosticsName = "DJI Drone View Runtime Diagnostics";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void RegisterSceneCallbacks()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
        SceneManager.activeSceneChanged -= OnActiveSceneChanged;
        SceneManager.activeSceneChanged += OnActiveSceneChanged;
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (scene.name != DroneViewSceneName)
            return;

        InstallForDroneView();
    }

    private static void OnActiveSceneChanged(Scene previousScene, Scene nextScene)
    {
        Debug.Log($"ACTIVE_SCENE_CHANGED {previousScene.name} -> {nextScene.name}");
    }

    private static void InstallForDroneView()
    {
        DisableLegacyTestNavigation();

        var background = Object.FindFirstObjectByType<DJIGPUBackground>(FindObjectsInactive.Include);
        if (background == null)
        {
            var cameraObject = new GameObject(
                DroneCameraName,
                typeof(Camera),
                typeof(AudioListener),
                typeof(UniversalAdditionalCameraData));
            cameraObject.tag = "MainCamera";

            var camera = cameraObject.GetComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;

            background = cameraObject.AddComponent<DJIGPUBackground>();
            var backgroundShader = Shader.Find("DJI/OESBackgroundURP");
            if (backgroundShader != null)
                background.backgroundMat = new Material(backgroundShader);

            background.verboseLogs = false;
            Debug.Log("DJI_BOOTSTRAP_STATE=VIDEO_CAMERA_CREATED");
        }
        else
        {
            Debug.Log($"DJI_BOOTSTRAP_STATE=EXISTING_VIDEO_BACKGROUND object={background.gameObject.name}");
        }

        if (Object.FindFirstObjectByType<DjiDroneViewRuntimeDiagnostics>(FindObjectsInactive.Include) == null)
        {
            var diagnosticsObject = new GameObject(RuntimeDiagnosticsName);
            diagnosticsObject.AddComponent<DjiDroneViewRuntimeDiagnostics>();
        }
    }

    private static void DisableLegacyTestNavigation()
    {
        var legacyNavigation = GameObject.Find("Deprecated DroneView Test Navigation");
        if (legacyNavigation != null)
            legacyNavigation.SetActive(false);
    }
}

internal sealed class DjiDroneViewRuntimeDiagnostics : MonoBehaviour
{
    private void Start()
    {
        Debug.Log("DRONEVIEW_SCENE_STARTED");
        PersistentReferenceFrameScene2Diagnostics.LogForDroneView();
        LogDjiPipelineState("SCENE_START");
        StartCoroutine(LogDelayedPipelineState());
    }

    private System.Collections.IEnumerator LogDelayedPipelineState()
    {
        yield return new WaitForSecondsRealtime(2f);
        LogDjiPipelineState("AFTER_2_SECONDS");
        yield return new WaitForSecondsRealtime(18f);
        LogDjiPipelineState("AFTER_20_SECONDS");
    }

    private static void LogDjiPipelineState(string checkpoint)
    {
        var bootstrap = Object.FindFirstObjectByType<DJIBootstrap>(FindObjectsInactive.Include);
        var background = Object.FindFirstObjectByType<DJIGPUBackground>(FindObjectsInactive.Include);
        Debug.Log(
            $"DJI_BOOTSTRAP_STATE={checkpoint} sceneBootstrapPresent={bootstrap != null} " +
            $"videoBackgroundPresent={background != null}");
        Debug.Log(
            $"DJI_VIDEO_PIPELINE_STATE={checkpoint} ready={background != null && background.IsReady} " +
            $"externalTextureId={(background != null ? background.ExternalTextureId : 0)}");
    }
}
