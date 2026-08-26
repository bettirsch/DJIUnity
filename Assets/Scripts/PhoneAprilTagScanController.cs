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
    private const float PoseCorrectionLerp = 0.45f;

    [SerializeField] private ARCameraManager cameraManager;
    [SerializeField] [Min(0.1f)] private float detectionIntervalSeconds = 0.1f;
    [SerializeField] [Min(1)] private int confirmationsRequired = 3;
    [SerializeField] private int targetTagId;

    private readonly float[] _nativeDetection = new float[12];
    private readonly float[] _nativePose = new float[12];
    private Text _statusLabel;
    private Button _connectDroneButton;
    private GameObject _previewCube;
    private ARAnchorManager _anchorManager;
    private ARAnchor _previewAnchor;
    private ARTrackedImageManager _trackedImageManager;
    private Texture2D _runtimeReferenceTexture;
    private int _consecutiveMatches;
    private bool _markerConfirmed;
    private bool _hasTagPose;

    private void Awake()
    {
        DisableLegacyPlacementPrototype();
        cameraManager ??= FindAnyObjectByType<ARCameraManager>();
        _anchorManager ??= FindAnyObjectByType<ARAnchorManager>();
        if (_anchorManager != null)
            _anchorManager.enabled = true;
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
                var pixels = new NativeArray<byte>(image.GetConvertedDataSize(conversion), Allocator.Temp);
                bool detected;
                try
                {
                    image.Convert(conversion, pixels);
                    var rgbaBytes = pixels.ToArray();
                    if (cameraManager.TryGetIntrinsics(out var intrinsics))
                    {
                        var imageToIntrinsicsScale = new Vector2(
                            conversion.outputDimensions.x / (float)intrinsics.resolution.x,
                            conversion.outputDimensions.y / (float)intrinsics.resolution.y);
                        detected = DJIAprilTagNative.TryDetectPose(
                            rgbaBytes,
                            conversion.outputDimensions.x,
                            conversion.outputDimensions.y,
                            intrinsics.focalLength.x * imageToIntrinsicsScale.x,
                            intrinsics.focalLength.y * imageToIntrinsicsScale.y,
                            intrinsics.principalPoint.x * imageToIntrinsicsScale.x,
                            intrinsics.principalPoint.y * imageToIntrinsicsScale.y,
                            PrintedTagSizeMeters,
                            _nativeDetection,
                            _nativePose);
                        _hasTagPose = detected;
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
                        SetStatus(_hasTagPose
                            ? "Marker rögzítve. Tartsa az AprilTag-et a telefon kameraképében."
                            : "Marker felismerve, de a telefon kamerakalibrációja még nem érhető el.");
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
        if (targetCamera == null || !_hasTagPose)
            return;

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

        var tagPosition = new Vector3(_nativePose[0], _nativePose[1], -_nativePose[2]);
        var tagRight = new Vector3(_nativePose[3], _nativePose[6], -_nativePose[9]).normalized;
        var tagUp = new Vector3(_nativePose[4], _nativePose[7], -_nativePose[10]).normalized;
        var tagNormal = Vector3.Cross(tagRight, tagUp).normalized;
        if (tagRight.sqrMagnitude < 0.9f || tagUp.sqrMagnitude < 0.9f || tagNormal.sqrMagnitude < 0.9f)
            return;

        var cubeLocalPosition = tagPosition + tagNormal * (PreviewCubeSizeMeters * 0.5f);
        var cubeLocalRotation = Quaternion.LookRotation(tagNormal, tagUp);
        var cubeWorldPosition = targetCamera.transform.TransformPoint(cubeLocalPosition);
        var cubeWorldRotation = targetCamera.transform.rotation * cubeLocalRotation;

        if (_previewAnchor != null)
        {
            var correctedLocalPosition = _previewAnchor.transform.InverseTransformPoint(cubeWorldPosition);
            var correctedLocalRotation = Quaternion.Inverse(_previewAnchor.transform.rotation) * cubeWorldRotation;
            _previewCube.transform.localPosition = Vector3.Lerp(
                _previewCube.transform.localPosition,
                correctedLocalPosition,
                PoseCorrectionLerp);
            _previewCube.transform.localRotation = Quaternion.Slerp(
                _previewCube.transform.localRotation,
                correctedLocalRotation,
                PoseCorrectionLerp);
            return;
        }

        var anchorObject = new GameObject("Phone AprilTag Anchor");
        anchorObject.transform.SetPositionAndRotation(cubeWorldPosition, cubeWorldRotation);
        _previewAnchor = anchorObject.AddComponent<ARAnchor>();

        if (!_previewAnchor.isActiveAndEnabled)
        {
            Destroy(anchorObject);
            _previewAnchor = null;
            SetStatus("Az ARCore ankor nem indult el. Irányítsa újra a telefont az AprilTag-re.");
            return;
        }

        _previewCube.transform.SetParent(_previewAnchor.transform, false);
        _previewCube.transform.localPosition = Vector3.zero;
        _previewCube.transform.localRotation = Quaternion.identity;
        _previewCube.transform.localScale = Vector3.one * PreviewCubeSizeMeters;
        _previewCube.SetActive(true);
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
