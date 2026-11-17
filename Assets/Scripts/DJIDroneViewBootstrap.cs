using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

internal static class DJIDroneViewBootstrap
{
    internal const string DroneViewSceneName = "DroneView";
    private const string DroneCameraName = "DJI Drone View Camera";
    private const string RuntimeDiagnosticsName = "DJI Drone View Runtime Diagnostics";
    private const string CameraPoseProviderName = "DJI Camera Telemetry Pose Provider";
    private const string SatelliteIndicatorName = "DJI Satellite Indicator";
    private const string BatteryIndicatorName = "DJI Aircraft Battery Indicator";
    private const string BoardVisionProviderName = "DJI Reference Board Vision Provider";
    private const string WorldLocalizationShadowName = "DJI World Localization Shadow";
    private const string SharedCubeOverlayName = "DJI Shared Reference Cube Overlay";

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
        Debug.Log("DRONE_MODE_STARTUP mode=STANDALONE_BOARD_FRAME");

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

        if (Object.FindFirstObjectByType<DjiCameraPoseProvider>(FindObjectsInactive.Include) == null)
        {
            var poseProviderObject = new GameObject(CameraPoseProviderName);
            var poseProvider = poseProviderObject.AddComponent<DjiCameraPoseProvider>();
            poseProvider.ConfigureDiagnosticLogging(true, 1f);
            Debug.Log("DJI_BOOTSTRAP_STATE=CAMERA_TELEMETRY_PROVIDER_CREATED");
        }

        if (Object.FindFirstObjectByType<DjiSatelliteIndicator>(FindObjectsInactive.Include) == null)
        {
            var satelliteIndicatorObject = new GameObject(SatelliteIndicatorName);
            satelliteIndicatorObject.AddComponent<DjiSatelliteIndicator>();
            Debug.Log("DJI_BOOTSTRAP_STATE=SATELLITE_INDICATOR_CREATED source=existing_position_audit_snapshot");
        }

        if (Object.FindFirstObjectByType<DjiAircraftBatteryIndicator>(FindObjectsInactive.Include) == null)
        {
            var batteryIndicatorObject = new GameObject(BatteryIndicatorName);
            batteryIndicatorObject.AddComponent<DjiAircraftBatteryIndicator>();
            Debug.Log("DJI_BOOTSTRAP_STATE=BATTERY_INDICATOR_CREATED source=aircraft_battery_snapshot");
        }

        var boardVisionProvider = Object.FindFirstObjectByType<DjiBoardVisionProvider>(FindObjectsInactive.Include);
        if (boardVisionProvider == null)
        {
            var boardVisionObject = new GameObject(BoardVisionProviderName);
            boardVisionProvider = boardVisionObject.AddComponent<DjiBoardVisionProvider>();
            Debug.Log("DJI_BOOTSTRAP_STATE=REFERENCE_BOARD_VISION_PROVIDER_CREATED");
        }

        // Reapply the deterministic policy for a scene-authored provider too.
        // This happens before it is wired to world localization or rendering.
        boardVisionProvider.ApplyDroneViewStartupDefaults();

        var worldLocalizationShadow = Object.FindFirstObjectByType<DjiWorldLocalizationShadow>(FindObjectsInactive.Include);
        if (worldLocalizationShadow == null)
        {
            var shadowObject = new GameObject(WorldLocalizationShadowName);
            worldLocalizationShadow = shadowObject.AddComponent<DjiWorldLocalizationShadow>();
        }
        worldLocalizationShadow.Configure(boardVisionProvider);
        boardVisionProvider.ConfigureWorldLocalizationShadow(worldLocalizationShadow);

        var sharedCubeOverlay = Object.FindFirstObjectByType<DjiSharedReferenceCubeOverlay>(FindObjectsInactive.Include);
        if (sharedCubeOverlay == null)
        {
            var overlayObject = new GameObject(SharedCubeOverlayName);
            sharedCubeOverlay = overlayObject.AddComponent<DjiSharedReferenceCubeOverlay>();
        }
        sharedCubeOverlay.Configure(background.GetComponent<Camera>(), boardVisionProvider);

        var trackingIndicator = Object.FindFirstObjectByType<DjiTrackingStatusIndicator>(FindObjectsInactive.Include);
        if (trackingIndicator == null)
            trackingIndicator = new GameObject("DJI Tracking Status Indicator").AddComponent<DjiTrackingStatusIndicator>();
        trackingIndicator.Configure(sharedCubeOverlay, worldLocalizationShadow);

        var presentationControls = Object.FindFirstObjectByType<DjiModelPresentationControls>(FindObjectsInactive.Include);
        if (presentationControls == null)
            presentationControls = new GameObject("DJI Model Presentation Controls").AddComponent<DjiModelPresentationControls>();
        presentationControls.Configure(sharedCubeOverlay);

        var screenshot = Object.FindFirstObjectByType<DjiDroneViewScreenshot>(FindObjectsInactive.Include);
        if (screenshot == null)
            screenshot = new GameObject("DJI Drone View Screenshot").AddComponent<DjiDroneViewScreenshot>();
        screenshot.Configure(background, sharedCubeOverlay);
    }

}

internal sealed class DjiDroneViewRuntimeDiagnostics : MonoBehaviour
{
    private void Start()
    {
        Debug.Log("DRONEVIEW_SCENE_STARTED");
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
