using System;
using System.Collections;
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
    [SerializeField] private ReferenceActionUi referenceActionUi;

    [Header("Diagnostics")]
    [SerializeField] private bool debugLogging = true;
    [SerializeField, Min(0.1f)] private float debugLogInterval = 1f;

    private ScanState _state;
    private ARAnchor _anchor;
    private GameObject _cube;
    private Renderer _cubeRenderer;
    private bool _anchorCreationInProgress;
    private TrackingState _lastTrackingState = TrackingState.None;
    private bool _hasSeenReferenceImage;
    private bool _targetImageAdded;
    private bool _targetReachedTracking;
    private bool _anchorCreationFailed;
    private bool _sceneTransitionInProgress;
    private float _nextDebugLogTime;
    private int _scanGeneration;

    public ARTrackedImageManager ConfiguredTrackedImageManager => trackedImageManager;
    public ARAnchorManager ConfiguredAnchorManager => anchorManager;
    public string TargetReferenceImageName => referenceImageName;
    public float ConfiguredImageWidthMeters => configuredImageWidthMeters;

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
        _ = PersistentReferenceFrame.Instance;
        referenceActionUi ??= ReferenceActionUi.FindOrCreate();
        PrepareUi();
        SetState(ScanState.Searching, "Tartsa a referencia-képet a telefon kameraképében.");
    }

    private void OnEnable()
    {
        if (trackedImageManager != null)
            trackedImageManager.trackablesChanged.AddListener(OnTrackablesChanged);
    }

    private void Start()
    {
        StartCoroutine(RunStartupDiagnostics());
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
        PersistentReferenceFrame.Instance.ResetReferencePose();

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
        _cube = null;
        _cubeRenderer = null;
        _hasSeenReferenceImage = false;
        _targetImageAdded = false;
        _targetReachedTracking = false;
        _anchorCreationFailed = false;
        _lastTrackingState = TrackingState.None;
        SetConnectDroneButtonVisible(false);
        SetRescanButtonVisible(false);
        SetState(ScanState.Searching, "Tartsa a referencia-képet a telefon kameraképében.");
    }

    public void Configure(
        ARTrackedImageManager imageManager,
        ARAnchorManager newAnchorManager,
        Canvas canvas,
        Text newStatusText,
        ReferenceActionUi newReferenceActionUi,
        float physicalWidthMeters)
    {
        trackedImageManager = imageManager;
        anchorManager = newAnchorManager;
        overlayCanvas = canvas;
        statusText = newStatusText;
        referenceActionUi = newReferenceActionUi;
        configuredImageWidthMeters = physicalWidthMeters;
    }

    private void OnTrackablesChanged(ARTrackablesChangedEventArgs<ARTrackedImage> changes)
    {
        foreach (var trackedImage in changes.added)
        {
            LogTrackedImageEvent("TRACKED_IMAGE_ADDED", trackedImage);
            if (IsConfiguredReferenceImage(trackedImage))
                _targetImageAdded = true;
            ProcessTrackedImage(trackedImage);
        }

        foreach (var trackedImage in changes.updated)
        {
            LogTrackedImageEvent("TRACKED_IMAGE_UPDATED", trackedImage);
            ProcessTrackedImage(trackedImage);
        }

        foreach (var removedTrackable in changes.removed)
        {
            var trackedImage = removedTrackable.Value;
            LogTrackedImageEvent("TRACKED_IMAGE_REMOVED", trackedImage);
            if (IsConfiguredReferenceImage(trackedImage))
                HandleTrackingState(TrackingState.None);
        }
    }

    private void ProcessTrackedImage(ARTrackedImage trackedImage)
    {
        if (!IsConfiguredReferenceImage(trackedImage))
            return;

        HandleTrackingState(trackedImage.trackingState);
        if (trackedImage.trackingState == TrackingState.Tracking)
            _targetReachedTracking = true;

        if (trackedImage.trackingState != TrackingState.Tracking || _anchor != null || _anchorCreationInProgress)
            return;

        _anchorCreationInProgress = true;
        Debug.Log($"[Reference Image] ACQUIRE_POSE_STARTED name={trackedImage.referenceImage.name} position={trackedImage.transform.position} rotation={trackedImage.transform.rotation.eulerAngles}");
        Debug.Log($"[Reference Image] ANCHOR_CREATE_REQUESTED name={trackedImage.referenceImage.name}");
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
                _anchorCreationFailed = true;
                Debug.LogWarning($"[Reference Image] ANCHOR_CREATE_FAILED status={result.status}");
                SetState(ScanState.Searching, "A referencia-kép megvan, de az AR rögzítés nem sikerült. Próbálja újra.");
                return;
            }

            _anchor = result.value;
            _anchor.gameObject.name = "ReferenceImageAnchor";
            CreateContentHierarchy(_anchor.transform);
            Debug.Log($"[Reference Image] ANCHOR_CREATED position={_anchor.transform.position} rotation={_anchor.transform.rotation.eulerAngles}");
            AcquirePersistentReferenceFrame(_anchor.transform);
            SetConnectDroneButtonVisible(true);
            SetRescanButtonVisible(true);
            referenceActionUi?.LogConfiguration("REFERENCE_ACQUIRED");
            SetState(ScanState.Anchored, "Referencia-kép rögzítve. Csatlakoztassa a drónt.");
        }
        catch (Exception exception)
        {
            _anchorCreationFailed = true;
            Debug.LogWarning($"[Reference Image] ANCHOR_CREATE_FAILED exception={exception.Message}");
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

        _cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        _cube.name = "Cube";
        _cube.transform.SetParent(contentAlignment, false);
        _cube.transform.localScale = Vector3.one * cubeSizeMeters;
        // The cube's local bottom face rests on the reference-image plane at local Y = 0.
        _cube.transform.localPosition = Vector3.up * (cubeSizeMeters * 0.5f);

        _cubeRenderer = _cube.GetComponent<Renderer>();
        if (_cubeRenderer != null)
            _cubeRenderer.material = CreateRuntimeMaterial(cubeColor);

        Debug.Log($"[Reference Image] CUBE_CREATED hierarchy={_cube.transform.GetHierarchyPath()} localPosition={_cube.transform.localPosition} localScale={_cube.transform.localScale}");
        Debug.Log($"[Reference Image] CUBE_ACTIVE activeSelf={_cube.activeSelf} activeInHierarchy={_cube.activeInHierarchy}");
        Debug.Log($"[Reference Image] CUBE_RENDERER_ENABLED rendererFound={_cubeRenderer != null} enabled={_cubeRenderer != null && _cubeRenderer.enabled}");
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

        DisableOverlayRaycasts();
        DisableLegacyActionButtons();
        referenceActionUi ??= ReferenceActionUi.FindOrCreate();
        referenceActionUi.Configure(OnConnectDroneClicked, ResetScan);
        SetConnectDroneButtonVisible(false);
        SetRescanButtonVisible(false);

        SetObjectActiveByName("Prototype Warning", false);
        SetObjectActiveByName("Center Reticle", false);
        SetObjectActiveByName("AR Placement Indicator", false);
        referenceActionUi.LogConfiguration("STARTUP");
    }

    private void LoadDroneView()
    {
        if (_sceneTransitionInProgress)
            return;

        if (!PersistentReferenceFrame.Instance.HasReferencePose)
        {
            Debug.LogWarning("[Persistent Reference] SCENE_TRANSITION_BLOCKED_NO_REFERENCE");
            SetStatus("A referencia-kép rögzítése szükséges a drónnézet megnyitásához.");
            return;
        }

        if (!Application.CanStreamedLevelBeLoaded("DroneView"))
        {
            Debug.LogError("[Persistent Reference] SCENE_TRANSITION_BLOCKED reason=DRONE_VIEW_NOT_IN_BUILD");
            SetStatus("A DroneView jelenet nincs benne az Android buildben.");
            return;
        }

        _sceneTransitionInProgress = true;
        Debug.Log("REFERENCE_FRAME_HANDOFF_OK");
        Debug.Log("[Persistent Reference] SCENE_TRANSITION_ALLOWED destination=DroneView");
        Debug.Log("DJI_SCENE_LOAD_REQUESTED DroneView");
        SceneManager.LoadScene(DJIDroneViewBootstrap.DroneViewSceneName);
    }

    public void OnConnectDroneClicked()
    {
        Debug.Log("CONNECT_DRONE_BUTTON_CLICKED");
        LoadDroneView();
    }

    [ContextMenu("Debug Load DroneView")]
    public void DebugLoadDroneView()
    {
        var persistentReferenceFrame = PersistentReferenceFrame.Instance;
        var loadable = Application.CanStreamedLevelBeLoaded("DroneView");
        Debug.Log("DEBUG_DRONEVIEW_LOAD_REQUESTED");
        Debug.Log($"PersistentReferenceFrame.HasReferencePose={persistentReferenceFrame.HasReferencePose}");
        Debug.Log($"DroneViewLoadable={loadable}");
        if (!loadable)
        {
            Debug.LogError("DRONEVIEW_NOT_LOADABLE");
            return;
        }

        Debug.Log("SCENE_TRANSITION_LOGIC_CONFIRMED");
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

    private void SetConnectDroneButtonVisible(bool visible)
    {
        if (referenceActionUi != null)
            referenceActionUi.SetConnectDroneVisible(visible);
    }

    private void SetRescanButtonVisible(bool visible)
    {
        if (referenceActionUi != null)
            referenceActionUi.SetRescanReferenceVisible(visible);
    }

    private void DisableOverlayRaycasts()
    {
        if (overlayCanvas == null)
            return;

        foreach (var graphic in overlayCanvas.GetComponentsInChildren<Graphic>(true))
            graphic.raycastTarget = false;

        foreach (var canvasGroup in overlayCanvas.GetComponentsInChildren<CanvasGroup>(true))
            canvasGroup.blocksRaycasts = false;
    }

    private static void DisableLegacyActionButtons()
    {
        SetObjectActiveByName("PlaceButton", false);
        SetObjectActiveByName("ResetButton", false);
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

    private void AcquirePersistentReferenceFrame(Transform worldFromTrackedImage)
    {
        var persistentReferenceFrame = PersistentReferenceFrame.Instance;
        if (persistentReferenceFrame.HasReferencePose)
        {
            Debug.Log("[Persistent Reference] REFERENCE_FRAME_ALREADY_ACQUIRED preservingExistingPose=true");
            return;
        }

        var worldFromReference = ConvertTrackedImagePoseToReferencePose(worldFromTrackedImage);
        persistentReferenceFrame.SetReferencePose(worldFromReference);
        Debug.Log(
            $"[Persistent Reference] REFERENCE_FRAME_ACQUIRED " +
            $"position={worldFromReference.position} rotation={worldFromReference.rotation.eulerAngles}");
    }

    private static Pose ConvertTrackedImagePoseToReferencePose(Transform worldFromTrackedImage)
    {
        // ARTrackedImage uses +X right, +Y image-plane normal, and +Z opposite board-up.
        // Convert it once to the documented reference board axes: +X right, +Y board-up, +Z outward.
        var worldReferenceRight = worldFromTrackedImage.right;
        var worldReferenceUp = -worldFromTrackedImage.forward;
        var worldReferenceForward = worldFromTrackedImage.up;
        var worldReferenceRotation = Quaternion.LookRotation(worldReferenceForward, worldReferenceUp);

        return new Pose(worldFromTrackedImage.position, worldReferenceRotation);
    }

    private IEnumerator RunStartupDiagnostics()
    {
        // Wait for AR Foundation to create the runtime image library before inspecting it.
        yield return null;

        var library = trackedImageManager != null ? trackedImageManager.referenceLibrary : null;
        var libraryCount = library?.count ?? 0;
        var containsTarget = false;
        var droneViewLoadable = Application.CanStreamedLevelBeLoaded("DroneView");

        Debug.Log($"[Reference Image] STARTUP trackedImageManagerEnabled={trackedImageManager != null && trackedImageManager.enabled} managerActive={trackedImageManager != null && trackedImageManager.gameObject.activeInHierarchy} controllerActive={gameObject.activeInHierarchy}");
        Debug.Log($"[Reference Image] STARTUP referenceLibraryAssigned={library != null} referenceLibraryCount={libraryCount} requestedMaxNumberOfMovingImages={trackedImageManager?.requestedMaxNumberOfMovingImages ?? -1} currentMaxNumberOfMovingImages={trackedImageManager?.currentMaxNumberOfMovingImages ?? -1}");
        Debug.Log($"[Reference Image] STARTUP arSessionState={ARSession.state} notTrackingReason={ARSession.notTrackingReason} imageTrackingSubsystemAvailable={trackedImageManager != null && trackedImageManager.subsystem != null} imageTrackingSubsystemRunning={trackedImageManager != null && trackedImageManager.subsystem != null && trackedImageManager.subsystem.running}");
        Debug.Log($"[Reference Image] STARTUP droneViewLoadable={droneViewLoadable}");

        for (var index = 0; index < libraryCount; index++)
        {
            var referenceImage = library[index];
            var matchesTarget = referenceImage.name == referenceImageName;
            containsTarget |= matchesTarget;
            Debug.Log($"[Reference Image] RUNTIME_LIBRARY_IMAGE index={index} name={referenceImage.name} size={referenceImage.size} specifiedSize={referenceImage.specifySize} matchesBuildingReference={matchesTarget}");
        }

        Debug.Log($"[Reference Image] STARTUP buildingReferenceExists={containsTarget} targetReferenceImageName={referenceImageName} expectedPhysicalWidthMeters={configuredImageWidthMeters:F3} applicationId={Application.identifier} version={Application.version} unityVersion={Application.unityVersion}");
        Debug.Log("[Reference Image] BUILD_NOTE The Android player must be rebuilt with Build And Run after changing BuildingReference.png or BuildingReferenceImageLibrary.asset.");

        yield return new WaitForSecondsRealtime(10f);
        ReportAcquisitionDiagnosis();
    }

    private void ReportAcquisitionDiagnosis()
    {
        if (!_targetImageAdded)
        {
            Debug.LogWarning("[Reference Image] DIAGNOSIS_A_RUNTIME_LIBRARY_OR_IMAGE_RECOGNITION_FAILURE reason=NO_TRACKED_IMAGE_ADDED_FOR_BuildingReference anchorWasNotRequested=true");
            return;
        }

        if (!_targetReachedTracking)
        {
            Debug.LogWarning("[Reference Image] DIAGNOSIS_B_TRACKED_IMAGE_NEVER_REACHES_TRACKING reason=BuildingReference_added_but_not_tracking");
            return;
        }

        if (_anchor == null)
        {
            var reason = _anchorCreationFailed ? "ANCHOR_CREATE_FAILED" : "ANCHOR_NOT_RETURNED";
            Debug.LogWarning($"[Reference Image] DIAGNOSIS_C_TRACKING_WORKS_BUT_ANCHOR_CREATION_FAILS reason={reason}");
            return;
        }

        if (_cube == null || _cubeRenderer == null || !_cube.activeInHierarchy || !_cubeRenderer.enabled)
        {
            Debug.LogError($"[Reference Image] DIAGNOSIS_D_ANCHOR_EXISTS_BUT_CUBE_RENDERING_OR_HIERARCHY_FAILS cubeExists={_cube != null} rendererExists={_cubeRenderer != null} cubeActive={_cube != null && _cube.activeInHierarchy} rendererEnabled={_cubeRenderer != null && _cubeRenderer.enabled}");
            return;
        }

        Debug.Log("[Reference Image] DIAGNOSIS_READY tracking_anchor_and_cube_are_active");
    }

    private void LogTrackedImageEvent(string eventName, ARTrackedImage trackedImage)
    {
        if (trackedImage == null)
        {
            Debug.Log($"[Reference Image] {eventName} image=null");
            return;
        }

        var referenceName = trackedImage.referenceImage.name;
        Debug.Log($"[Reference Image] {eventName} name={referenceName} trackingState={trackedImage.trackingState} position={trackedImage.transform.position} rotation={trackedImage.transform.rotation.eulerAngles} estimatedSize={trackedImage.size} matchesBuildingReference={referenceName == referenceImageName}");
    }
}

internal static class ReferenceImageTransformDiagnostics
{
    public static string GetHierarchyPath(this Transform transform)
    {
        var path = transform.name;
        while (transform.parent != null)
        {
            transform = transform.parent;
            path = $"{transform.name}/{path}";
        }

        return path;
    }
}
