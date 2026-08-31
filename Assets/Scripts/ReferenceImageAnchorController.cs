using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

[DisallowMultipleComponent]
[RequireComponent(typeof(ARTrackedImageManager))]
[RequireComponent(typeof(ARAnchorManager))]
public sealed class ReferenceImageAnchorController : MonoBehaviour
{
    private enum ScanState
    {
        Searching,
        AcquirePose,
        Anchored
    }

    [Header("Reference image")]
    [SerializeField] private string referenceImageName = "BuildingReference";
    [SerializeField, Min(0.01f)] private float configuredImageWidthMeters = 0.16f;

    [Header("Content")]
    [SerializeField, Min(0.01f)] private float cubeSizeMeters = 0.14f;
    [SerializeField] private Color cubeColor = new Color(0.12f, 0.74f, 0.58f, 1f);

    [Header("Scene wiring")]
    [SerializeField] private ARTrackedImageManager trackedImageManager;
    [SerializeField] private ARAnchorManager anchorManager;
    [SerializeField] private Canvas overlayCanvas;
    [SerializeField] private Text statusText;
    [SerializeField] private Button connectDroneButton;
    [SerializeField] private Button resetButton;

    [Header("Diagnostics")]
    [SerializeField] private bool debugLogging;
    [SerializeField, Min(0.1f)] private float debugLogInterval = 1f;

    private ScanState _state;
    private ARAnchor _anchor;
    private bool _anchorCreationInProgress;
    private TrackingState _lastTrackingState = TrackingState.None;
    private bool _hasSeenReferenceImage;
    private float _nextDebugLogTime;
    private int _scanGeneration;

    private void Awake()
    {
        trackedImageManager ??= GetComponent<ARTrackedImageManager>();
        anchorManager ??= GetComponent<ARAnchorManager>();

        if (trackedImageManager == null || anchorManager == null)
        {
            Debug.LogError("[Reference Image] Missing ARTrackedImageManager or ARAnchorManager.");
            enabled = false;
            return;
        }

        PreparePhoneCameraScanning();
        PrepareUi();
        SetState(ScanState.Searching, "Tartsa a referencia-képet a telefon kameraképében.");
    }

    private void OnEnable()
    {
        if (trackedImageManager != null)
            trackedImageManager.trackablesChanged.AddListener(OnTrackablesChanged);
    }

    private void OnDisable()
    {
        if (trackedImageManager != null)
            trackedImageManager.trackablesChanged.RemoveListener(OnTrackablesChanged);
    }

    private void Update()
    {
        if (!debugLogging || _anchor == null || Time.unscaledTime < _nextDebugLogTime)
            return;

        _nextDebugLogTime = Time.unscaledTime + debugLogInterval;
        Debug.Log($"[Reference Image] state={_state} anchorPosition={_anchor.transform.position} anchorRotation={_anchor.transform.rotation.eulerAngles}");
    }

    public void ResetScan()
    {
        _scanGeneration++;
        _anchorCreationInProgress = false;

        if (_anchor != null)
        {
            Debug.Log("[Reference Image] ANCHOR_RESET reason=USER_REQUEST");
            var removed = false;
            if (anchorManager != null && anchorManager.enabled)
                removed = anchorManager.TryRemoveAnchor(_anchor);

            if (!removed)
                Destroy(_anchor.gameObject);
        }

        _anchor = null;
        _hasSeenReferenceImage = false;
        _lastTrackingState = TrackingState.None;
        SetConnectButtonVisible(false);
        SetResetButtonVisible(false);
        SetState(ScanState.Searching, "Tartsa a referencia-képet a telefon kameraképében.");
    }

    public void Configure(
        ARTrackedImageManager imageManager,
        ARAnchorManager newAnchorManager,
        Canvas canvas,
        Text newStatusText,
        Button newConnectDroneButton,
        Button newResetButton,
        float physicalWidthMeters)
    {
        trackedImageManager = imageManager;
        anchorManager = newAnchorManager;
        overlayCanvas = canvas;
        statusText = newStatusText;
        connectDroneButton = newConnectDroneButton;
        resetButton = newResetButton;
        configuredImageWidthMeters = physicalWidthMeters;
    }

    private void OnTrackablesChanged(ARTrackablesChangedEventArgs<ARTrackedImage> changes)
    {
        foreach (var trackedImage in changes.added)
            ProcessTrackedImage(trackedImage);

        foreach (var trackedImage in changes.updated)
            ProcessTrackedImage(trackedImage);

        foreach (var trackedImage in changes.removed)
        {
            if (IsConfiguredReferenceImage(trackedImage))
                HandleTrackingState(TrackingState.None);
        }
    }

    private void ProcessTrackedImage(ARTrackedImage trackedImage)
    {
        if (!IsConfiguredReferenceImage(trackedImage))
            return;

        HandleTrackingState(trackedImage.trackingState);
        if (trackedImage.trackingState != TrackingState.Tracking || _anchor != null || _anchorCreationInProgress)
            return;

        _anchorCreationInProgress = true;
        SetState(ScanState.AcquirePose, "Referencia-kép felismerve. A pozíció rögzítése folyamatban van.");
        CreateAnchorAsync(new Pose(trackedImage.transform.position, trackedImage.transform.rotation), _scanGeneration);
    }

    private async Awaitable CreateAnchorAsync(Pose imagePose, int requestGeneration)
    {
        try
        {
            var result = await anchorManager.TryAddAnchorAsync(imagePose);
            if (requestGeneration != _scanGeneration)
                return;

            if (!result.status.IsSuccess() || result.value == null)
            {
                Debug.LogWarning($"[Reference Image] Anchor creation failed: {result.status}");
                SetState(ScanState.Searching, "A referencia-kép megvan, de az AR rögzítés nem sikerült. Próbálja újra.");
                return;
            }

            _anchor = result.value;
            _anchor.gameObject.name = "ReferenceImageAnchor";
            CreateContentHierarchy(_anchor.transform);
            Debug.Log($"[Reference Image] ANCHOR_CREATED position={_anchor.transform.position} rotation={_anchor.transform.rotation.eulerAngles}");
            SetConnectButtonVisible(true);
            SetResetButtonVisible(true);
            SetState(ScanState.Anchored, "Referencia-kép rögzítve. Csatlakoztassa a drónt.");
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[Reference Image] Anchor creation exception: {exception.Message}");
            if (requestGeneration == _scanGeneration)
                SetState(ScanState.Searching, "Az AR rögzítés nem elérhető. Mozgassa lassan a telefont, majd próbálja újra.");
        }
        finally
        {
            if (requestGeneration == _scanGeneration)
                _anchorCreationInProgress = false;
        }
    }

    private void CreateContentHierarchy(Transform anchorTransform)
    {
        var contentAlignment = new GameObject("ContentAlignment").transform;
        contentAlignment.SetParent(anchorTransform, false);
        // AR Foundation image pose uses local +Y as the image-plane normal. No Euler correction is applied.
        contentAlignment.localPosition = Vector3.zero;
        contentAlignment.localRotation = Quaternion.identity;

        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = "Cube";
        cube.transform.SetParent(contentAlignment, false);
        cube.transform.localScale = Vector3.one * cubeSizeMeters;
        // The cube's local bottom face rests on the reference-image plane at local Y = 0.
        cube.transform.localPosition = Vector3.up * (cubeSizeMeters * 0.5f);

        var renderer = cube.GetComponent<Renderer>();
        if (renderer != null)
            renderer.material = CreateRuntimeMaterial(cubeColor);
    }

    private void HandleTrackingState(TrackingState trackingState)
    {
        if (_lastTrackingState == trackingState)
            return;

        _lastTrackingState = trackingState;
        switch (trackingState)
        {
            case TrackingState.Tracking:
                if (!_hasSeenReferenceImage)
                {
                    _hasSeenReferenceImage = true;
                    Debug.Log("[Reference Image] REFERENCE_IMAGE_FOUND name=BuildingReference");
                }

                Debug.Log("[Reference Image] REFERENCE_IMAGE_TRACKING");
                break;

            case TrackingState.Limited:
                Debug.Log("[Reference Image] REFERENCE_IMAGE_LIMITED");
                PreserveAnchor("LIMITED");
                break;

            default:
                Debug.Log("[Reference Image] REFERENCE_IMAGE_LOST");
                PreserveAnchor("NOT_TRACKING");
                break;
        }
    }

    private void PreserveAnchor(string reason)
    {
        if (_anchor == null)
            return;

        Debug.Log($"[Reference Image] ANCHOR_PRESERVED reason={reason}");
        if (_state == ScanState.Anchored)
            SetStatus("Referencia-kép átmenetileg nem követhető. A rögzített tartalom a helyén marad.");
    }

    private bool IsConfiguredReferenceImage(ARTrackedImage trackedImage)
    {
        return trackedImage != null && trackedImage.referenceImage.name == referenceImageName;
    }

    private void PreparePhoneCameraScanning()
    {
        trackedImageManager.enabled = true;
        anchorManager.enabled = true;

        var arCamera = FindFirstObjectByType<ARCameraManager>();
        if (arCamera != null)
        {
            arCamera.enabled = true;
            arCamera.autoFocusRequested = true;
            arCamera.requestedFacingDirection = CameraFacingDirection.World;

            var background = arCamera.GetComponent<ARCameraBackground>();
            if (background != null)
                background.enabled = true;

            var djiBackground = arCamera.GetComponent<DJIGPUBackground>();
            if (djiBackground != null)
                djiBackground.enabled = false;

            var djiPoseDriver = arCamera.GetComponent<DJICameraPoseDriver>();
            if (djiPoseDriver != null)
                djiPoseDriver.enabled = false;
        }
    }

    private void PrepareUi()
    {
        if (overlayCanvas != null)
            overlayCanvas.gameObject.SetActive(true);

        SetConnectButtonVisible(false);
        SetResetButtonVisible(false);
        if (connectDroneButton != null)
        {
            connectDroneButton.onClick.RemoveAllListeners();
            connectDroneButton.onClick.AddListener(LoadDroneView);
            SetButtonLabel(connectDroneButton, "Csatlakoztassa a drónt");
        }

        if (resetButton != null)
        {
            resetButton.onClick.RemoveAllListeners();
            resetButton.onClick.AddListener(ResetScan);
            SetButtonLabel(resetButton, "Új keresés");
        }

        SetObjectActiveByName("Prototype Warning", false);
        SetObjectActiveByName("Center Reticle", false);
        SetObjectActiveByName("AR Placement Indicator", false);
    }

    private void LoadDroneView()
    {
        SceneManager.LoadScene("DroneView");
    }

    private void SetState(ScanState state, string status)
    {
        _state = state;
        SetStatus(status);
        if (debugLogging)
            Debug.Log($"[Reference Image] State={state} configuredWidthMeters={configuredImageWidthMeters:F3}");
    }

    private void SetStatus(string status)
    {
        if (statusText != null)
            statusText.text = status;
    }

    private void SetConnectButtonVisible(bool visible)
    {
        if (connectDroneButton != null)
            connectDroneButton.gameObject.SetActive(visible);
    }

    private void SetResetButtonVisible(bool visible)
    {
        if (resetButton != null)
            resetButton.gameObject.SetActive(visible);
    }

    private static void SetButtonLabel(Button button, string text)
    {
        var label = button.GetComponentInChildren<Text>(true);
        if (label != null)
            label.text = text;
    }

    private static void SetObjectActiveByName(string objectName, bool active)
    {
        var target = GameObject.Find(objectName);
        if (target != null)
            target.SetActive(active);
    }

    private static Material CreateRuntimeMaterial(Color color)
    {
        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var material = new Material(shader);
        if (material.HasProperty("_BaseColor"))
            material.SetColor("_BaseColor", color);
        else if (material.HasProperty("_Color"))
            material.SetColor("_Color", color);

        return material;
    }
}
