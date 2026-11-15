using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Owns Android screen-timeout policy for the app's scene-based camera modes.
/// It intentionally uses Unity's Screen API rather than a platform WakeLock.
/// </summary>
[DisallowMultipleComponent]
public sealed class KeepScreenAwakeInCameraMode : MonoBehaviour
{
    private const string HomeSceneName = "Boot";
    private const string DroneViewSceneName = "DroneView";

    private static KeepScreenAwakeInCameraMode _instance;
    private string _lastAppliedSceneName;
    private int _lastAppliedTimeout = int.MinValue;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void EnsureService()
    {
        if (_instance != null)
            return;

        var serviceObject = new GameObject(nameof(KeepScreenAwakeInCameraMode));
        DontDestroyOnLoad(serviceObject);
        _instance = serviceObject.AddComponent<KeepScreenAwakeInCameraMode>();
    }

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        DontDestroyOnLoad(gameObject);
        SceneManager.activeSceneChanged += OnActiveSceneChanged;
        ApplyForScene(SceneManager.GetActiveScene());
    }

    private void OnDestroy()
    {
        if (_instance != this)
            return;

        SceneManager.activeSceneChanged -= OnActiveSceneChanged;
        Screen.sleepTimeout = SleepTimeout.SystemSetting;
        _instance = null;
    }

    private void OnActiveSceneChanged(Scene previousScene, Scene nextScene)
    {
        ApplyForScene(nextScene);
    }

    private void ApplyForScene(Scene scene)
    {
        var isCameraMode = scene.name == DroneViewSceneName;
        var targetTimeout = isCameraMode ? SleepTimeout.NeverSleep : SleepTimeout.SystemSetting;

        if (_lastAppliedSceneName == scene.name && _lastAppliedTimeout == targetTimeout)
            return;

        Screen.sleepTimeout = targetTimeout;
        _lastAppliedSceneName = scene.name;
        _lastAppliedTimeout = targetTimeout;

        var mode = isCameraMode ? "NEVER_SLEEP" : "SYSTEM_SETTING";
        var sceneLabel = scene.name == HomeSceneName ? HomeSceneName : scene.name;
        Debug.Log($"SCREEN_SLEEP_MODE={mode} scene={sceneLabel}");
    }
}
