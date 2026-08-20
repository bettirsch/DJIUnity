using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.XR.ARSubsystems;
using UnityEngine.InputSystem.XR;
#endif

[DisallowMultipleComponent]
public sealed class DJIAprilTagMarkerMvpController : MonoBehaviour
{
    private const string RuntimeCanvasName = "DJI AprilTag MVP Canvas";
    private const string RuntimeStatusName = "DJI AprilTag MVP Status";
    private const string RuntimeCubeName = "DJI AprilTag Dummy Cube";

    [Header("Camera")]
    [SerializeField] private Camera targetCamera;
    [SerializeField] private DJIGPUBackground djiGpuBackground;

    [Header("Detection")]
    [SerializeField] [Min(0.1f)] private float detectionIntervalSeconds = 0.45f;
    [SerializeField] [Min(0.1f)] private float lostTrackingHideDelaySeconds = 1.0f;
    [SerializeField] private int targetTagId;

    [Header("Cube")]
    [SerializeField] [Min(0.25f)] private float cubeDistanceMeters = 3.0f;
    [SerializeField] private Vector3 cubeScale = new Vector3(0.35f, 0.35f, 0.35f);
    [SerializeField] private Color cubeColor = new Color(0.12f, 0.92f, 0.72f, 1.0f);

    [Header("Debug")]
    [SerializeField] private bool verboseLogs;

    private readonly float[] _nativeDetection = new float[12];
    private Coroutine _detectionLoop;
    private Text _statusLabel;
    private GameObject _cube;
    private float _lastDetectionAt = float.NegativeInfinity;
    private string _lastStatusMessage;

    private void Awake()
    {
        if (targetCamera == null)
            targetCamera = GetComponent<Camera>() ?? Camera.main;

        if (djiGpuBackground == null && targetCamera != null)
            djiGpuBackground = targetCamera.GetComponent<DJIGPUBackground>();

        DisablePhoneArPrototype();
        EnsureStatusUi();
        EnsureCube();
    }

    private void OnEnable()
    {
        DJIAprilTagNative.SetTargetTagId(targetTagId);

        if (_detectionLoop == null)
            _detectionLoop = StartCoroutine(DetectionLoop());
    }

    private void OnDisable()
    {
        if (_detectionLoop != null)
        {
            StopCoroutine(_detectionLoop);
            _detectionLoop = null;
        }

        DJIAprilTagNative.ReleaseDetector();
    }

    private IEnumerator DetectionLoop()
    {
#if !UNITY_ANDROID || UNITY_EDITOR
        UpdateStatus("AprilTag MVP detection is available only in the Android player build.");
        yield break;
#else
        while (enabled)
        {
            if (targetCamera == null)
            {
                UpdateStatus("No camera available for AprilTag MVP.");
                yield return new WaitForSecondsRealtime(detectionIntervalSeconds);
                continue;
            }

            if (djiGpuBackground != null && !djiGpuBackground.IsReady)
            {
                UpdateStatus("Waiting for DJI video feed before AprilTag search...");
                HideCubeIfTrackingExpired(force: true);
                yield return new WaitForSecondsRealtime(detectionIntervalSeconds);
                continue;
            }

            UpdateStatus($"Searching for AprilTag {targetTagId}...");
            yield return new WaitForEndOfFrame();

            var screenshot = ScreenCapture.CaptureScreenshotAsTexture();
            if (screenshot == null)
            {
                UpdateStatus("Screen capture failed while checking the DJI feed.");
                yield return new WaitForSecondsRealtime(detectionIntervalSeconds);
                continue;
            }

            try
            {
                var rgbaBytes = screenshot.GetRawTextureData<byte>().ToArray();
                var detected = DJIAprilTagNative.TryDetect(rgbaBytes, screenshot.width, screenshot.height, _nativeDetection);

                if (detected)
                    ApplyDetection();
                else
                    HideCubeIfTrackingExpired(force: false);
            }
            finally
            {
                Destroy(screenshot);
            }

            yield return new WaitForSecondsRealtime(detectionIntervalSeconds);
        }
#endif
    }

    private void ApplyDetection()
    {
        if (_cube == null || targetCamera == null)
            return;

        var viewportX = Mathf.Clamp01(_nativeDetection[1]);
        var viewportY = 1.0f - Mathf.Clamp01(_nativeDetection[2]);
        var worldPosition = targetCamera.ViewportToWorldPoint(new Vector3(viewportX, viewportY, cubeDistanceMeters));

        if (_cube.transform.parent != targetCamera.transform)
            _cube.transform.SetParent(targetCamera.transform, true);

        _cube.transform.localPosition = targetCamera.transform.InverseTransformPoint(worldPosition);
        _cube.transform.localRotation = Quaternion.identity;
        _cube.transform.localScale = cubeScale;
        _cube.SetActive(true);
        _lastDetectionAt = Time.unscaledTime;

        var detectedTagId = Mathf.RoundToInt(_nativeDetection[0]);
        var decisionMargin = _nativeDetection[11];
        UpdateStatus($"AprilTag {detectedTagId} locked. margin={decisionMargin:F1}");

        if (verboseLogs)
        {
            Debug.Log(
                $"[AprilTag MVP] tagId={detectedTagId} center=({_nativeDetection[1]:F3}, {_nativeDetection[2]:F3}) " +
                $"margin={decisionMargin:F2}"
            );
        }
    }

    private void HideCubeIfTrackingExpired(bool force)
    {
        if (_cube == null)
            return;

        if (!force && Time.unscaledTime - _lastDetectionAt < lostTrackingHideDelaySeconds)
            return;

        _cube.SetActive(false);
        UpdateStatus($"Searching for AprilTag {targetTagId}...");
    }

    private void EnsureCube()
    {
        if (_cube != null)
            return;

        _cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        _cube.name = RuntimeCubeName;
        _cube.transform.localScale = cubeScale;
        _cube.SetActive(false);

        var collider = _cube.GetComponent<Collider>();
        if (collider != null)
            Destroy(collider);

        var cubeRenderer = _cube.GetComponent<Renderer>();
        if (cubeRenderer != null)
        {
            cubeRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            cubeRenderer.receiveShadows = false;
            cubeRenderer.material.color = cubeColor;
        }
    }

    private void DisablePhoneArPrototype()
    {
        var placementController = FindAnyObjectByType<ARPlacementPrototypeController>();
        if (placementController != null)
            placementController.enabled = false;

        var oldCanvas = GameObject.Find("AR Placement Canvas");
        if (oldCanvas != null)
            oldCanvas.SetActive(false);

        var legacyMarker = FindAnyObjectByType<TapToPlaceMarker>();
        if (legacyMarker != null)
            legacyMarker.enabled = false;

        DisableIfPresent<ARSession>();
        DisableIfPresent<ARPlaneManager>();
        DisableIfPresent<ARRaycastManager>();
        DisableIfPresent<ARAnchorManager>();
        DisableIfPresent<ARCameraManager>();
        DisableIfPresent<ARCameraBackground>();
        DisableIfPresent<UnityEngine.InputSystem.XR.TrackedPoseDriver>();

        var eventSystem = FindAnyObjectByType<EventSystem>();
        if (eventSystem != null)
            eventSystem.enabled = false;

#if ENABLE_INPUT_SYSTEM
        var inputModule = FindAnyObjectByType<InputSystemUIInputModule>();
        if (inputModule != null)
            inputModule.enabled = false;
#endif
    }

    private void EnsureStatusUi()
    {
        var canvasObject = GameObject.Find(RuntimeCanvasName);
        if (canvasObject == null)
        {
            canvasObject = new GameObject(RuntimeCanvasName);
            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 900;

            var scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            canvasObject.AddComponent<GraphicRaycaster>();
        }

        var statusObject = GameObject.Find(RuntimeStatusName);
        if (statusObject == null)
        {
            statusObject = new GameObject(RuntimeStatusName);
            statusObject.transform.SetParent(canvasObject.transform, false);

            var rectTransform = statusObject.AddComponent<RectTransform>();
            rectTransform.anchorMin = new Vector2(0.5f, 1.0f);
            rectTransform.anchorMax = new Vector2(0.5f, 1.0f);
            rectTransform.pivot = new Vector2(0.5f, 1.0f);
            rectTransform.anchoredPosition = new Vector2(0f, -18f);
            rectTransform.sizeDelta = new Vector2(900f, 72f);

            var background = statusObject.AddComponent<Image>();
            background.color = new Color(0.05f, 0.08f, 0.12f, 0.76f);

            var textObject = new GameObject("Label");
            textObject.transform.SetParent(statusObject.transform, false);

            var textRect = textObject.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(18f, 12f);
            textRect.offsetMax = new Vector2(-18f, -12f);

            _statusLabel = textObject.AddComponent<Text>();
            _statusLabel.alignment = TextAnchor.MiddleCenter;
            _statusLabel.fontSize = 24;
            _statusLabel.color = Color.white;
            _statusLabel.horizontalOverflow = HorizontalWrapMode.Wrap;
            _statusLabel.verticalOverflow = VerticalWrapMode.Truncate;
            _statusLabel.font = LoadBuiltinFont();
        }
        else
        {
            _statusLabel = statusObject.GetComponentInChildren<Text>(true);
        }
    }

    private static Font LoadBuiltinFont()
    {
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        return font != null ? font : Resources.GetBuiltinResource<Font>("Arial.ttf");
    }

    private void UpdateStatus(string message)
    {
        if (string.Equals(message, _lastStatusMessage))
            return;

        _lastStatusMessage = message;

        if (_statusLabel != null)
            _statusLabel.text = message;
    }

    private static void DisableIfPresent<T>() where T : Behaviour
    {
        var component = FindAnyObjectByType<T>();
        if (component != null)
            component.enabled = false;
    }
}

internal static class DJIAprilTagMarkerMvpBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        var camera = Camera.main ?? Object.FindAnyObjectByType<Camera>();
        if (camera == null || camera.GetComponent<DJIAprilTagMarkerMvpController>() != null)
            return;

        camera.gameObject.AddComponent<DJIAprilTagMarkerMvpController>();
    }
}
