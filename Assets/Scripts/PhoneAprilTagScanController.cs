using System.Collections;
using Unity.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Unity.XR.CoreUtils;

[DisallowMultipleComponent]
public sealed class PhoneAprilTagScanController : MonoBehaviour
{
    private const string RuntimeCanvasName = "Phone AprilTag Scan Canvas";
    private const string DroneViewSceneName = "DroneView";
    private const string PreviewCubeName = "Phone AprilTag Preview Cube";
    private const string ReferenceImageResourceName = "AprilTagReference";
    private const float ReferenceImageWidthMeters = 0.2f;
    private const float PrintedTagSizeMeters = 0.2f;
    private const float PreviewCubeSizeMeters = 0.14f;
    private const int MaxDetectionImageDimension = 960;
    private const float MarkerLostTimeoutSeconds = 0.2f;

    [SerializeField] private ARCameraManager cameraManager;
    [SerializeField] [Min(0.01f)] private float detectionIntervalSeconds = 0.03f;
    [SerializeField] [Min(1)] private int confirmationsRequired = 3;
    [SerializeField] private int targetTagId;

    private readonly float[] _nativeDetection = new float[12];
    private readonly float[] _nativePose = new float[12];
    private Text _statusLabel;
    private Button _connectDroneButton;
    private GameObject _previewCube;
    private ARTrackedImageManager _trackedImageManager;
    private Texture2D _runtimeReferenceTexture;
    private int _consecutiveMatches;
    private bool _markerConfirmed;
    private bool _hasTagPose;
    private float _lastPoseUpdateTime = float.NegativeInfinity;
    private Vector2Int _lastDetectionImageSize;
    private float _lastDetectionFx;

    private void Awake()
    {
        DisableLegacyPlacementPrototype();
        cameraManager ??= FindAnyObjectByType<ARCameraManager>();
        CreateUi();
        AprilTagScanSession.Clear();
    }

    private void OnEnable()
    {
        DJIAprilTagNative.SetTargetTagId(targetTagId);
        StartCoroutine(ScanLoop());
    }

    private void OnDisable()
    {
        StopAllCoroutines();
        if (_trackedImageManager != null)
            _trackedImageManager.trackablesChanged.RemoveListener(OnTrackedImagesChanged);
        DJIAprilTagNative.ReleaseDetector();
    }

    private void Update()
    {
        // A stale pose would make the cube appear attached even after the marker leaves the frame.
        if (_previewCube != null && _previewCube.activeSelf &&
            Time.unscaledTime - _lastPoseUpdateTime > MarkerLostTimeoutSeconds)
            _previewCube.SetActive(false);
    }

    private IEnumerator InitializeImageTracking()
    {
#if !UNITY_ANDROID || UNITY_EDITOR
        SetStatus("Az ARCore image tracking az Android buildben működik.");
        yield break;
#else
        SetStatus("AprilTag image tracker indítása...");
        while (ARSession.state == ARSessionState.None || ARSession.state == ARSessionState.CheckingAvailability)
            yield return null;

        if (ARSession.state < ARSessionState.Ready)
        {
            SetStatus("Az ARCore image tracking nem érhető el ezen a készüléken.");
            yield break;
        }

        var origin = FindAnyObjectByType<XROrigin>();
        if (origin == null)
        {
            SetStatus("Az XR Origin nem érhető el.");
            yield break;
        }

        _trackedImageManager = origin.GetComponent<ARTrackedImageManager>();
        if (_trackedImageManager == null)
        {
            _trackedImageManager = origin.gameObject.AddComponent<ARTrackedImageManager>();
            _trackedImageManager.enabled = false;
        }

        var imageBytes = Resources.Load<TextAsset>(ReferenceImageResourceName);
        if (imageBytes == null)
        {
            SetStatus("Az AprilTag referencia kép hiányzik a buildből.");
            yield break;
        }

        _runtimeReferenceTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
        if (!_runtimeReferenceTexture.LoadImage(imageBytes.bytes, false))
        {
            SetStatus("Az AprilTag referencia kép nem tölthető be.");
            yield break;
        }

        RuntimeReferenceImageLibrary runtimeLibrary;
        try
        {
            runtimeLibrary = _trackedImageManager.CreateRuntimeLibrary();
        }
        catch (System.Exception exception)
        {
            SetStatus($"Az ARCore image tracker nem indítható: {exception.Message}");
            yield break;
        }

        if (runtimeLibrary is not MutableRuntimeReferenceImageLibrary mutableLibrary)
        {
            SetStatus("Ez az ARCore eszköz nem támogatja a runtime marker library-t.");
            yield break;
        }

        var addImage = mutableLibrary.ScheduleAddImageWithValidationJob(
            _runtimeReferenceTexture,
            "AprilTag-A4-0",
            ReferenceImageWidthMeters);
        yield return new WaitUntil(() => addImage.status.IsComplete());
        if (!addImage.status.IsSuccess())
        {
            SetStatus($"Az AprilTag referencia kép elutasítva: {addImage.status}.");
            yield break;
        }

        _trackedImageManager.referenceLibrary = runtimeLibrary;
        _trackedImageManager.requestedMaxNumberOfMovingImages = 1;
        _trackedImageManager.trackablesChanged.AddListener(OnTrackedImagesChanged);
        _trackedImageManager.enabled = true;
        SetStatus("Irányítsa a telefon kameráját az A4 AprilTag markerre.");
#endif
    }

    private void OnTrackedImagesChanged(ARTrackablesChangedEventArgs<ARTrackedImage> changes)
    {
        foreach (var trackedImage in changes.added)
            UpdateTrackedImagePreview(trackedImage);

        foreach (var trackedImage in changes.updated)
            UpdateTrackedImagePreview(trackedImage);
    }

    private void UpdateTrackedImagePreview(ARTrackedImage trackedImage)
    {
        if (trackedImage.trackingState != TrackingState.Tracking)
        {
            if (_previewCube != null)
                _previewCube.SetActive(false);
            return;
        }

        if (_previewCube == null)
        {
            _previewCube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _previewCube.name = PreviewCubeName;

            var collider = _previewCube.GetComponent<Collider>();
            if (collider != null)
                Destroy(collider);

            var cubeRenderer = _previewCube.GetComponent<Renderer>();
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Universal Render Pipeline/Lit");
            if (cubeRenderer != null && shader != null)
            {
                cubeRenderer.material = new Material(shader);
                cubeRenderer.material.color = new Color(1f, 0f, 0.78f, 1f);
                cubeRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                cubeRenderer.receiveShadows = false;
            }
        }

        _previewCube.transform.SetParent(trackedImage.transform, false);
        _previewCube.transform.localPosition = Vector3.forward * (PreviewCubeSizeMeters * 0.5f);
        _previewCube.transform.localRotation = Quaternion.identity;
        _previewCube.transform.localScale = Vector3.one * PreviewCubeSizeMeters;
        _previewCube.SetActive(true);

        if (!_markerConfirmed)
        {
            _markerConfirmed = true;
            AprilTagScanSession.Confirm(targetTagId);
            SetStatus("AprilTag rögzítve ARCore image trackinggel. Csatlakoztassa a drónt, majd folytassa.");
            _connectDroneButton.gameObject.SetActive(true);
        }
    }

    private IEnumerator ScanLoop()
    {
#if !UNITY_ANDROID || UNITY_EDITOR
        SetStatus("Az AprilTag beolvasás az Android buildben működik.");
        yield break;
#else
        while (true)
        {
            if (cameraManager == null)
            {
                SetStatus("A telefon kamerája nem érhető el.");
                yield return new WaitForSecondsRealtime(detectionIntervalSeconds);
                continue;
            }

            if (!cameraManager.TryAcquireLatestCpuImage(out var image))
            {
                SetStatus("Telefonkamera indítása...");
                yield return new WaitForSecondsRealtime(detectionIntervalSeconds);
                continue;
            }

            try
            {
                var conversion = new XRCpuImage.ConversionParams(image, TextureFormat.RGBA32);
                var largestImageDimension = Mathf.Max(image.width, image.height);
                if (largestImageDimension > MaxDetectionImageDimension)
                {
                    var scale = MaxDetectionImageDimension / (float)largestImageDimension;
                    conversion.outputDimensions = new Vector2Int(
                        Mathf.RoundToInt(image.width * scale),
                        Mathf.RoundToInt(image.height * scale));
                }
                var pixels = new NativeArray<byte>(image.GetConvertedDataSize(conversion), Allocator.Temp);
                bool detected;
                try
                {
                    image.Convert(conversion, pixels);
                    var rgbaBytes = pixels.ToArray();
                    if (TryGetCameraCalibration(conversion.outputDimensions, out var fx, out var fy, out var cx, out var cy))
                    {
                        _lastDetectionImageSize = conversion.outputDimensions;
                        _lastDetectionFx = fx;
                        detected = DJIAprilTagNative.TryDetectPose(
                            rgbaBytes,
                            conversion.outputDimensions.x,
                            conversion.outputDimensions.y,
                            fx,
                            fy,
                            cx,
                            cy,
                            PrintedTagSizeMeters,
                            _nativeDetection,
                            _nativePose);
                        _hasTagPose = detected;
                        if (!detected && Mathf.RoundToInt(_nativeDetection[0]) == targetTagId)
                            detected = true;
                    }
                    else
                    {
                        detected = DJIAprilTagNative.TryDetect(rgbaBytes, conversion.outputDimensions.x, conversion.outputDimensions.y, _nativeDetection);
                        _hasTagPose = false;
                    }
                }
                finally
                {
                    pixels.Dispose();
                }

                if (detected)
                {
                    if (!_markerConfirmed)
                    {
                        _consecutiveMatches++;
                        SetStatus($"AprilTag {targetTagId} felismerve ({_consecutiveMatches}/{confirmationsRequired})");
                        if (_consecutiveMatches >= confirmationsRequired)
                            ConfirmMarker();
                    }

                    if (_markerConfirmed)
                        ShowMarkerPreview();
                }
                else
                {
                    if (_markerConfirmed)
                    {
                        SetStatus("Tartsa az AprilTag-et a telefon kameraképében.");
                    }
                    else
                    {
                        _consecutiveMatches = 0;
                        SetStatus($"Irányítsa a telefon kameráját az AprilTag {targetTagId} markerre.");
                    }
                }
            }
            finally
            {
                image.Dispose();
            }

            yield return new WaitForSecondsRealtime(detectionIntervalSeconds);
        }
#endif
    }

    private bool TryGetCameraCalibration(Vector2Int imageSize, out float fx, out float fy, out float cx, out float cy)
    {
        if (cameraManager.TryGetIntrinsics(out var intrinsics) &&
            intrinsics.resolution.x > 0 && intrinsics.resolution.y > 0)
        {
            var imageToIntrinsicsScale = new Vector2(
                imageSize.x / (float)intrinsics.resolution.x,
                imageSize.y / (float)intrinsics.resolution.y);
            fx = intrinsics.focalLength.x * imageToIntrinsicsScale.x;
            fy = intrinsics.focalLength.y * imageToIntrinsicsScale.y;
            cx = intrinsics.principalPoint.x * imageToIntrinsicsScale.x;
            cy = intrinsics.principalPoint.y * imageToIntrinsicsScale.y;
            return fx > 0f && fy > 0f;
        }

        var arCamera = cameraManager.GetComponent<Camera>() ?? Camera.main;
        if (arCamera != null)
        {
            var projection = arCamera.projectionMatrix;
            fx = Mathf.Abs(projection.m00) * imageSize.x * 0.5f;
            fy = Mathf.Abs(projection.m11) * imageSize.y * 0.5f;
            if (fx > 0.001f && fy > 0.001f)
            {
                // CPU images can have a different orientation than the display, so use
                // the image center rather than display-space principal point offsets.
                cx = imageSize.x * 0.5f;
                cy = imageSize.y * 0.5f;
                return true;
            }
        }

        // Some ARCore devices expose CPU images before either intrinsics or a
        // usable projection matrix. A nominal rear-camera model still lets the
        // AprilTag solver produce a pose instead of disabling the preview.
        const float fallbackVerticalFieldOfViewDegrees = 60f;
        var halfVerticalFieldOfViewRadians = fallbackVerticalFieldOfViewDegrees * Mathf.Deg2Rad * 0.5f;
        fy = imageSize.y * 0.5f / Mathf.Tan(halfVerticalFieldOfViewRadians);
        fx = fy * imageSize.x / imageSize.y;
        cx = imageSize.x * 0.5f;
        cy = imageSize.y * 0.5f;
        return true;
    }

    private void ConfirmMarker()
    {
        _markerConfirmed = true;
        AprilTagScanSession.Confirm(targetTagId);
        ShowMarkerPreview();
        SetStatus("Marker rögzítve. Csatlakoztassa a drónt, majd folytassa.");
        _connectDroneButton.gameObject.SetActive(true);
    }

    private void ShowMarkerPreview()
    {
        var targetCamera = cameraManager != null ? cameraManager.GetComponent<Camera>() : Camera.main;
        if (targetCamera == null)
            return;

        EnsurePreviewCube();

        if (!_hasTagPose)
        {
            ShowCornerTrackedPreview(targetCamera);
            return;
        }

        var tagPosition = new Vector3(_nativePose[0], _nativePose[1], -_nativePose[2]);
        var tagRight = new Vector3(_nativePose[3], _nativePose[6], -_nativePose[9]).normalized;
        var tagUp = Vector3.ProjectOnPlane(
            new Vector3(_nativePose[4], _nativePose[7], -_nativePose[10]),
            tagRight).normalized;
        var tagNormal = Vector3.Cross(tagRight, tagUp).normalized;
        if (!IsFinite(tagPosition) || !IsFinite(tagRight) || !IsFinite(tagUp) || !IsFinite(tagNormal) ||
            tagRight.sqrMagnitude < 0.9f || tagUp.sqrMagnitude < 0.9f || tagNormal.sqrMagnitude < 0.9f)
        {
            ShowCornerTrackedPreview(targetCamera);
            return;
        }

        var cubeLocalPosition = tagPosition + tagNormal * (PreviewCubeSizeMeters * 0.5f);
        var cubeLocalRotation = Quaternion.LookRotation(tagNormal, tagUp);
        var cubeWorldPosition = targetCamera.transform.TransformPoint(cubeLocalPosition);
        var cubeWorldRotation = targetCamera.transform.rotation * cubeLocalRotation;

        // The AprilTag pose is recalculated from the current camera frame, so it must
        // drive the preview directly rather than being converted into a one-time AR anchor.
        _previewCube.transform.SetParent(null, true);
        _previewCube.transform.SetPositionAndRotation(cubeWorldPosition, cubeWorldRotation);
        _previewCube.transform.localScale = Vector3.one * PreviewCubeSizeMeters;
        _previewCube.SetActive(true);
        _lastPoseUpdateTime = Time.unscaledTime;
    }

    private void EnsurePreviewCube()
    {
        if (_previewCube != null)
            return;

        _previewCube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        _previewCube.name = PreviewCubeName;

        var collider = _previewCube.GetComponent<Collider>();
        if (collider != null)
            Destroy(collider);

        var cubeRenderer = _previewCube.GetComponent<Renderer>();
        var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Universal Render Pipeline/Lit");
        if (cubeRenderer != null && shader != null)
        {
            cubeRenderer.material = new Material(shader);
            cubeRenderer.material.color = new Color(1f, 0f, 0.78f, 1f);
            cubeRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            cubeRenderer.receiveShadows = false;
        }
    }

    private void ShowCornerTrackedPreview(Camera targetCamera)
    {
        if (_previewCube == null || _lastDetectionImageSize.x <= 0 || _lastDetectionImageSize.y <= 0 || _lastDetectionFx <= 0f)
            return;

        var topWidthPixels = Vector2.Distance(
            new Vector2(_nativeDetection[3] * _lastDetectionImageSize.x, _nativeDetection[4] * _lastDetectionImageSize.y),
            new Vector2(_nativeDetection[5] * _lastDetectionImageSize.x, _nativeDetection[6] * _lastDetectionImageSize.y));
        var bottomWidthPixels = Vector2.Distance(
            new Vector2(_nativeDetection[9] * _lastDetectionImageSize.x, _nativeDetection[10] * _lastDetectionImageSize.y),
            new Vector2(_nativeDetection[7] * _lastDetectionImageSize.x, _nativeDetection[8] * _lastDetectionImageSize.y));
        var tagWidthPixels = (topWidthPixels + bottomWidthPixels) * 0.5f;
        if (tagWidthPixels < 1f)
            return;

        var depth = Mathf.Clamp(PrintedTagSizeMeters * _lastDetectionFx / tagWidthPixels, 0.15f, 15f);
        var viewportPosition = new Vector3(
            Mathf.Clamp01(_nativeDetection[1]),
            Mathf.Clamp01(1f - _nativeDetection[2]),
            depth + PreviewCubeSizeMeters * 0.5f);

        _previewCube.transform.SetParent(null, true);
        _previewCube.transform.SetPositionAndRotation(
            targetCamera.ViewportToWorldPoint(viewportPosition),
            targetCamera.transform.rotation);
        _previewCube.transform.localScale = Vector3.one * PreviewCubeSizeMeters;
        _previewCube.SetActive(true);
        _lastPoseUpdateTime = Time.unscaledTime;
    }

    private static bool IsFinite(Vector3 value)
    {
        return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
               !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
               !float.IsNaN(value.z) && !float.IsInfinity(value.z);
    }

    private void LoadDroneView()
    {
        SceneManager.LoadScene(DroneViewSceneName, LoadSceneMode.Single);
    }

    private void DisableLegacyPlacementPrototype()
    {
        var placementController = FindAnyObjectByType<ARPlacementPrototypeController>();
        if (placementController != null)
            placementController.enabled = false;

        foreach (var planeManager in FindObjectsByType<ARPlaneManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            planeManager.enabled = false;

        foreach (var raycastManager in FindObjectsByType<ARRaycastManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            raycastManager.enabled = false;

        var djiBackground = FindAnyObjectByType<DJIGPUBackground>();
        if (djiBackground != null)
            djiBackground.enabled = false;

        var djiPoseDriver = FindAnyObjectByType<DJICameraPoseDriver>();
        if (djiPoseDriver != null)
            djiPoseDriver.enabled = false;

        var legacyCanvas = GameObject.Find("AR Placement Canvas");
        if (legacyCanvas != null)
            legacyCanvas.SetActive(false);
    }

    private void CreateUi()
    {
        var canvasObject = new GameObject(RuntimeCanvasName);
        var canvas = canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 1000;

        var scaler = canvasObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        canvasObject.AddComponent<GraphicRaycaster>();

        var panel = CreatePanel(canvas.transform, "Status", new Vector2(0.5f, 0f), new Vector2(0f, 72f), new Vector2(980f, 86f), new Color(0.04f, 0.07f, 0.09f, 0.72f));
        _statusLabel = CreateText(panel, "Label", 24, TextAnchor.MiddleCenter);

        var buttonPanel = CreatePanel(canvas.transform, "ConnectDroneButton", new Vector2(0.5f, 0f), new Vector2(0f, 184f), new Vector2(420f, 72f), new Color(0.05f, 0.42f, 0.32f, 0.96f));
        _connectDroneButton = buttonPanel.gameObject.AddComponent<Button>();
        _connectDroneButton.onClick.AddListener(LoadDroneView);
        var buttonText = CreateText(buttonPanel, "Label", 28, TextAnchor.MiddleCenter);
        buttonText.text = "Csatlakoztassa a drónt";
        _connectDroneButton.gameObject.SetActive(false);

        SetStatus($"Irányítsa a telefon kameráját az AprilTag {targetTagId} markerre.");
    }

    private static RectTransform CreatePanel(Transform parent, string name, Vector2 anchor, Vector2 position, Vector2 size, Color color)
    {
        var panelObject = new GameObject(name, typeof(RectTransform));
        panelObject.transform.SetParent(parent, false);
        var rect = panelObject.GetComponent<RectTransform>();
        rect.anchorMin = anchor;
        rect.anchorMax = anchor;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
        panelObject.AddComponent<Image>().color = color;
        return rect;
    }

    private static Text CreateText(RectTransform parent, string name, int fontSize, TextAnchor alignment)
    {
        var textObject = new GameObject(name, typeof(RectTransform));
        textObject.transform.SetParent(parent, false);
        var rect = textObject.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(18f, 10f);
        rect.offsetMax = new Vector2(-18f, -10f);

        var text = textObject.AddComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = fontSize;
        text.alignment = alignment;
        text.color = Color.white;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        return text;
    }

    private void SetStatus(string message)
    {
        if (_statusLabel != null)
            _statusLabel.text = message;
    }
}

internal static class PhoneAprilTagScanBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        if (SceneManager.GetActiveScene().name != "SampleScene")
            return;

        var camera = Camera.main ?? Object.FindAnyObjectByType<Camera>();
        if (camera != null && camera.GetComponent<PhoneAprilTagScanController>() == null)
            camera.gameObject.AddComponent<PhoneAprilTagScanController>();
    }
}
