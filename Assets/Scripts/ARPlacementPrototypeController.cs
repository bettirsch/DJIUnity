using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

/// <summary>
/// Phone-AR prototype placement flow.
///
/// IMPORTANT:
/// This controller intentionally uses AR Foundation / ARCore plane raycasts and anchors
/// from the Android phone tracking session. The visible background remains the DJI drone
/// camera feed rendered by the custom OES URP pipeline, so the placed content is NOT yet
/// registered to what the user sees in the DJI image.
///
/// A future solution must align the DJI camera and the phone AR session in a shared
/// coordinate system before any "place on the drone image" interaction can be trusted.
/// </summary>
public sealed class ARPlacementPrototypeController : MonoBehaviour
{
    private const string PrototypeWarningText =
        "Prototype placement uses the phone AR tracking coordinate system. It is not yet registered to the DJI camera feed.";

    private const string EventSystemName = "AR Placement EventSystem";
    private const string CanvasName = "AR Placement Canvas";
    private const string IndicatorName = "AR Placement Indicator";
    private const string FallbackCubeName = "Prototype Dummy Cube";
    private const string FallbackPlacementRootName = "Prototype Placement Root";

    [Header("Scene References")]
    [SerializeField] private Camera targetCamera;
    [SerializeField] private XROrigin xrOrigin;
    [SerializeField] private ARSession arSession;
    [SerializeField] private ARPlaneManager planeManager;
    [SerializeField] private ARRaycastManager raycastManager;
    [SerializeField] private ARAnchorManager anchorManager;
    [SerializeField] private ARCameraManager arCameraManager;
    [SerializeField] private ARCameraBackground arCameraBackground;
    [SerializeField] private DJIGPUBackground djiGpuBackground;

    [Header("Optional UI References")]
    [SerializeField] private Canvas overlayCanvas;
    [SerializeField] private Button placeButton;
    [SerializeField] private Button resetButton;
    [SerializeField] private Text warningText;
    [SerializeField] private Text statusText;
    [SerializeField] private RectTransform centerReticle;
    [SerializeField] private GameObject placementIndicatorObject;

    [Header("Optional Prefabs")]
    [SerializeField] private GameObject placementIndicatorPrefab;
    [SerializeField] private GameObject placedObjectPrefab;

    [Header("Placement")]
    [SerializeField] private bool autoCreateSceneDependencies = true;
    [SerializeField] private bool allowPlacementWithoutPlane = true;
    [SerializeField] private bool preferPlaneAttachedAnchorWhenAvailable;
    [SerializeField] [Min(0f)] private float indicatorSurfaceOffset = 0.01f;
    [SerializeField] [Min(0f)] private float placedObjectSurfacePadding = 0.01f;
    [SerializeField] [Min(0.1f)] private float fallbackPlacementDistance = 1.25f;
    [SerializeField] private Vector3 fallbackCubeScale = new Vector3(0.25f, 0.25f, 0.25f);

    [Header("Tracking Startup")]
    [SerializeField] private bool usePhoneCameraFeedWhileInitializing = true;
    [SerializeField] [Min(1f)] private float trackingStartupTimeout = 12f;
    [SerializeField] [Min(0.1f)] private float sessionRestartDelay = 0.75f;
    [SerializeField] [Range(0, 3)] private int maxAutomaticSessionResets = 1;
    [SerializeField] private bool restartSessionOnTimestampRegression;
    [SerializeField] private bool restartSessionWhenStartupIsInterrupted = true;
    [SerializeField] [Min(0f)] private float startupRecoveryDelay = 0.35f;

    [Header("Debug")]
    [SerializeField] private bool verboseLogs;

    private readonly List<ARRaycastHit> _raycastHits = new List<ARRaycastHit>();
    private readonly Vector2 _viewportCenter = new Vector2(0.5f, 0.5f);

    private GameObject _placementIndicatorInstance;
    private ARAnchor _placedAnchor;
    private GameObject _placedContent;
    private Pose _currentPlacementPose;
    private ARPlane _currentPlacementPlane;
    private bool _hasPlacementPose;
    private bool _isFallbackPlacementPose;
    private bool _isCreatingAnchor;
    private bool _loggedMissingPlacement;
    private string _lastStatusMessage;
    private ARSessionState _lastSessionState;
    private NotTrackingReason _lastNotTrackingReason;
    private bool _trackingEverEstablished;
    private bool _hasReceivedCameraFrame;
    private bool _cameraFrameTimestampRegressed;
    private bool _sessionRestartInProgress;
    private bool _presentingPhoneCameraFeed;
    private bool _applicationHasFocus = true;
    private bool _applicationPaused;
    private bool _startupWasInterrupted;
    private int _automaticSessionResetCount;
    private float _currentTrackingAttemptStartedAt;
    private float _lastCameraFrameReceivedAt;
    private long? _lastCameraFrameTimestampNs;
    private Coroutine _startupRecoveryCoroutine;

    private void Awake()
    {
        ResolveReferences();

        if (autoCreateSceneDependencies)
            EnsureSceneDependencies();

        DisableConflictingPrototypeComponents();
        ApplyTrackingStartupPreferences();
        UpdateFeatureStartupGates(forceDisableRaycast: ShouldGuideTrackingStartup(ARSession.state));
        EnsureOverlayUi();
        EnsurePlacementIndicator();
        UpdateWarningLabel();
        UpdateStatus("Move the phone to detect a horizontal plane.");
        RefreshUiState();
    }

    private void OnEnable()
    {
        if (placeButton != null)
            placeButton.onClick.AddListener(OnPlaceButtonPressed);

        if (resetButton != null)
            resetButton.onClick.AddListener(OnResetButtonPressed);

        if (arCameraManager != null)
            arCameraManager.frameReceived += OnCameraFrameReceived;

        ApplyTrackingStartupPreferences();
        BeginTrackingAttempt();
        UpdateFeatureStartupGates(forceDisableRaycast: ShouldGuideTrackingStartup(ARSession.state));
        ApplyCameraFeedPresentation(force: true);
    }

    private void OnDisable()
    {
        if (placeButton != null)
            placeButton.onClick.RemoveListener(OnPlaceButtonPressed);

        if (resetButton != null)
            resetButton.onClick.RemoveListener(OnResetButtonPressed);

        if (arCameraManager != null)
            arCameraManager.frameReceived -= OnCameraFrameReceived;

        StopStartupRecoveryRoutine();
    }

    private void Update()
    {
        UpdateTrackingStartup();
        UpdateSessionDiagnostics();
        UpdatePlacementPose();
        RefreshUiState();
    }

    private void ResolveReferences()
    {
        if (targetCamera == null)
            targetCamera = GetComponent<Camera>() ?? Camera.main;

        if (xrOrigin == null)
            xrOrigin = GetComponent<XROrigin>();

        if (planeManager == null)
            planeManager = GetComponent<ARPlaneManager>();

        if (raycastManager == null)
            raycastManager = GetComponent<ARRaycastManager>();

        if (anchorManager == null)
            anchorManager = GetComponent<ARAnchorManager>();

        if (arCameraManager == null && targetCamera != null)
            arCameraManager = targetCamera.GetComponent<ARCameraManager>();

        if (arCameraBackground == null && targetCamera != null)
            arCameraBackground = GetOrAddComponent<ARCameraBackground>(targetCamera.gameObject);

        if (djiGpuBackground == null && targetCamera != null)
            djiGpuBackground = targetCamera.GetComponent<DJIGPUBackground>();

        if (arSession == null)
            arSession = FindAnyObjectByType<ARSession>();
    }

    private void EnsureSceneDependencies()
    {
        if (targetCamera == null)
        {
            Debug.LogError("[AR Prototype] No target camera found. Placement prototype cannot initialize.");
            return;
        }

        xrOrigin = xrOrigin != null ? xrOrigin : GetOrAddComponent<XROrigin>(gameObject);
        xrOrigin.Camera = targetCamera;

        planeManager = planeManager != null ? planeManager : GetOrAddComponent<ARPlaneManager>(gameObject);
        planeManager.requestedDetectionMode = PlaneDetectionMode.Horizontal;

        raycastManager = raycastManager != null ? raycastManager : GetOrAddComponent<ARRaycastManager>(gameObject);
        anchorManager = anchorManager != null ? anchorManager : GetOrAddComponent<ARAnchorManager>(gameObject);
        arCameraManager = arCameraManager != null ? arCameraManager : GetOrAddComponent<ARCameraManager>(targetCamera.gameObject);
        arCameraBackground = arCameraBackground != null ? arCameraBackground : GetOrAddComponent<ARCameraBackground>(targetCamera.gameObject);
        djiGpuBackground = djiGpuBackground != null ? djiGpuBackground : targetCamera.GetComponent<DJIGPUBackground>();

        if (arSession == null)
        {
            var sessionObject = new GameObject("AR Session");
            arSession = sessionObject.AddComponent<ARSession>();
            sessionObject.AddComponent<ARInputManager>();
        }
        else if (arSession.GetComponent<ARInputManager>() == null)
        {
            arSession.gameObject.AddComponent<ARInputManager>();
        }

        if (verboseLogs)
        {
            Debug.Log(
                $"[AR Prototype] Scene dependencies ready. " +
                $"camera={targetCamera.name} origin={xrOrigin.name} session={arSession.name}"
            );
        }
    }

    private void OnApplicationFocus(bool hasFocus)
    {
        _applicationHasFocus = hasFocus;

        if (!hasFocus)
        {
            RegisterStartupInterruption("application focus was lost");
            return;
        }

        RecoverTrackingStartupAfterInterruption("application focus returned");
    }

    private void OnApplicationPause(bool pauseStatus)
    {
        _applicationPaused = pauseStatus;

        if (pauseStatus)
        {
            RegisterStartupInterruption("application was paused");
            return;
        }

        RecoverTrackingStartupAfterInterruption("application resumed");
    }

    private void DisableConflictingPrototypeComponents()
    {
        foreach (var legacyMarker in FindObjectsByType<TapToPlaceMarker>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            legacyMarker.enabled = false;

            foreach (var currentRenderer in legacyMarker.GetComponentsInChildren<Renderer>(true))
            {
                if (currentRenderer != null)
                    currentRenderer.enabled = false;
            }
        }

        if (targetCamera != null)
        {
            var poseDriver = targetCamera.GetComponent<DJICameraPoseDriver>();
            if (poseDriver != null && poseDriver.enabled)
            {
                poseDriver.enabled = false;
                Debug.LogWarning(
                    "[AR Prototype] Disabled DJICameraPoseDriver so AR Foundation can own the phone camera pose. " +
                    "The DJI video background remains active, but placement is not aligned to the drone feed yet."
                );
            }
        }
    }

    private void EnsureOverlayUi()
    {
        if (overlayCanvas == null)
            overlayCanvas = FindAnyObjectByType<Canvas>();

        if (overlayCanvas == null)
        {
            var canvasObject = new GameObject(CanvasName);
            overlayCanvas = canvasObject.AddComponent<Canvas>();
            overlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            overlayCanvas.sortingOrder = 1000;
            canvasObject.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            canvasObject.AddComponent<GraphicRaycaster>();
        }

        EnsureEventSystem();
        EnsureWarningLabel();
        EnsureStatusLabel();
        EnsureButtons();
        EnsureCenterReticle();
    }

    private void EnsureEventSystem()
    {
        if (EventSystem.current != null)
            return;

        var eventSystemObject = new GameObject(EventSystemName);
        eventSystemObject.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
        eventSystemObject.AddComponent<InputSystemUIInputModule>();
#else
        eventSystemObject.AddComponent<StandaloneInputModule>();
#endif
    }

    private void EnsureWarningLabel()
    {
        if (warningText != null)
            return;

        var panel = CreatePanel("Prototype Warning", overlayCanvas.transform as RectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -20f), new Vector2(980f, 120f));
        var image = panel.GetComponent<Image>();
        if (image != null)
            image.color = new Color(0.76f, 0.24f, 0.16f, 0.88f);

        warningText = CreateText("WarningText", panel, 24, TextAnchor.MiddleCenter, Color.white);
        warningText.rectTransform.offsetMin = new Vector2(18f, 18f);
        warningText.rectTransform.offsetMax = new Vector2(-18f, -18f);
    }

    private void EnsureStatusLabel()
    {
        if (statusText != null)
            return;

        var panel = CreatePanel("Placement Status", overlayCanvas.transform as RectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 72f), new Vector2(760f, 70f));
        var image = panel.GetComponent<Image>();
        if (image != null)
            image.color = new Color(0.08f, 0.1f, 0.14f, 0.78f);

        statusText = CreateText("StatusText", panel, 20, TextAnchor.MiddleCenter, Color.white);
        statusText.rectTransform.offsetMin = new Vector2(12f, 8f);
        statusText.rectTransform.offsetMax = new Vector2(-12f, -8f);
    }

    private void EnsureButtons()
    {
        if (placeButton != null && resetButton != null)
            return;

        var row = CreateEmptyRect("Placement Buttons", overlayCanvas.transform as RectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 18f), new Vector2(520f, 48f));

        if (placeButton == null)
            placeButton = CreateButton("PlaceButton", row, "Place", new Vector2(-110f, 0f));

        if (resetButton == null)
            resetButton = CreateButton("ResetButton", row, "Reset", new Vector2(110f, 0f));
    }

    private void EnsureCenterReticle()
    {
        if (centerReticle != null)
            return;

        centerReticle = CreateEmptyRect("Center Reticle", overlayCanvas.transform as RectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(56f, 56f));
        CreateReticleBar(centerReticle, "ReticleHorizontal", new Vector2(26f, 3f));
        CreateReticleBar(centerReticle, "ReticleVertical", new Vector2(3f, 26f));
    }

    private void EnsurePlacementIndicator()
    {
        if (_placementIndicatorInstance != null)
            return;

        if (placementIndicatorObject != null)
        {
            _placementIndicatorInstance = placementIndicatorObject;
            _placementIndicatorInstance.name = IndicatorName;
            _placementIndicatorInstance.SetActive(false);
            return;
        }

        if (placementIndicatorPrefab != null)
        {
            _placementIndicatorInstance = Instantiate(placementIndicatorPrefab, transform);
        }
        else
        {
            _placementIndicatorInstance = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            _placementIndicatorInstance.name = IndicatorName;
            _placementIndicatorInstance.transform.SetParent(transform, false);
            _placementIndicatorInstance.transform.localScale = new Vector3(0.16f, 0.004f, 0.16f);

            var collider = _placementIndicatorInstance.GetComponent<Collider>();
            if (collider != null)
                Destroy(collider);

            var renderer = _placementIndicatorInstance.GetComponent<Renderer>();
            if (renderer != null)
                renderer.sharedMaterial = CreateRuntimeMaterial(new Color(0.2f, 0.85f, 0.3f, 0.95f));
        }

        _placementIndicatorInstance.SetActive(false);
    }

    private void UpdatePlacementPose()
    {
        if (targetCamera == null)
        {
            SetPlacementAvailability(false, default, null, false);
            return;
        }

        if (ShouldHoldPlacementUntilTracking())
        {
            SetPlacementAvailability(false, default, null, false);
            return;
        }

        var screenPoint = targetCamera.ViewportToScreenPoint(new Vector3(_viewportCenter.x, _viewportCenter.y, 0f));
        if (raycastManager != null && raycastManager.Raycast(screenPoint, _raycastHits, TrackableType.PlaneWithinPolygon))
        {
            _loggedMissingPlacement = false;
            var hit = _raycastHits[0];
            var plane = planeManager != null ? planeManager.GetPlane(hit.trackableId) : null;
            SetPlacementAvailability(true, hit.pose, plane, false);
            return;
        }

        if (!_loggedMissingPlacement && verboseLogs)
        {
            _loggedMissingPlacement = true;
            Debug.Log("[AR Prototype] No horizontal plane hit at screen centre.");
        }

        if (TryGetFallbackPlacementPose(out var fallbackPose))
        {
            SetPlacementAvailability(true, fallbackPose, null, true);
            return;
        }

        SetPlacementAvailability(false, default, null, false);
    }

    private void SetPlacementAvailability(bool available, Pose pose, ARPlane plane, bool isFallbackPlacement)
    {
        _hasPlacementPose = available;
        _currentPlacementPose = pose;
        _currentPlacementPlane = plane;
        _isFallbackPlacementPose = available && isFallbackPlacement;

        if (_placementIndicatorInstance != null)
        {
            _placementIndicatorInstance.SetActive(available);
            if (available)
            {
                _placementIndicatorInstance.transform.SetPositionAndRotation(
                    pose.position + pose.up * indicatorSurfaceOffset,
                    pose.rotation
                );
            }
        }

        if (available)
        {
            if (_isFallbackPlacementPose)
            {
                UpdateStatus(_placedContent == null
                    ? "No plane yet. Press Place to drop a temporary test object in front of the camera."
                    : "No plane yet. Press Place to replace the temporary test object.");
            }
            else
            {
                UpdateStatus(_placedAnchor == null
                    ? "Horizontal plane found. Press Place to create a phone-AR test anchor."
                    : "Horizontal plane found. Press Place to replace the current test anchor.");
            }
        }
        else if (!_isCreatingAnchor)
        {
            UpdateStatus(_placedContent == null
                ? BuildIdleStatusMessage(ARSession.state, ARSession.notTrackingReason)
                : "Reset the current placement or move the phone to detect another horizontal plane.");
        }
    }

    private void RefreshUiState()
    {
        if (placeButton != null)
            placeButton.interactable = _hasPlacementPose && !_isCreatingAnchor;

        if (resetButton != null)
            resetButton.interactable = _placedAnchor != null || _placedContent != null;
    }

    public void OnPlaceButtonPressed()
    {
        if (_isCreatingAnchor)
            return;

        if (!_hasPlacementPose)
        {
            Debug.LogWarning("[AR Prototype] Place requested without a valid plane hit.");
            UpdateStatus("No valid placement pose is available yet.");
            return;
        }

        _ = PlaceSelectionAsync(_currentPlacementPose, _currentPlacementPlane);
    }

    public void OnResetButtonPressed()
    {
        ClearPlacedContent();
        UpdateStatus("Placement cleared. Move the phone to detect a new horizontal plane.");
        RefreshUiState();
    }

    private async Task PlaceSelectionAsync(Pose placementPose, ARPlane placementPlane)
    {
        _isCreatingAnchor = true;
        RefreshUiState();
        UpdateStatus(_isFallbackPlacementPose ? "Creating temporary placement..." : "Creating anchor...");

        ClearPlacedContent();

        ARAnchor anchor = null;
        var canTryAnchor = anchorManager != null &&
            (placementPlane != null || ARSession.state == ARSessionState.SessionTracking);

        if (preferPlaneAttachedAnchorWhenAvailable && placementPlane != null && anchorManager != null)
        {
            anchor = anchorManager.AttachAnchor(placementPlane, placementPose);
            if (anchor == null && verboseLogs)
                Debug.LogWarning("[AR Prototype] Plane-attached anchor failed, falling back to TryAddAnchorAsync.");
        }

        if (anchor == null && canTryAnchor)
        {
            try
            {
                var result = await anchorManager.TryAddAnchorAsync(placementPose);
                if (!result.status.IsSuccess() || result.value == null)
                {
                    Debug.LogWarning($"[AR Prototype] TryAddAnchorAsync failed: {result.status}. Falling back to an unanchored placement.");
                }
                else
                {
                    anchor = result.value;
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[AR Prototype] Exception while creating anchor. Falling back to an unanchored placement: " + exception);
            }
        }

        var content = CreatePlacedContent();
        if (content == null)
        {
            Debug.LogError("[AR Prototype] Unable to create placement content.");
            if (anchor != null)
                Destroy(anchor.gameObject);
            UpdateStatus("Content creation failed.");
            _isCreatingAnchor = false;
            RefreshUiState();
            return;
        }

        if (anchor != null)
        {
            content.transform.SetParent(anchor.transform, false);
            content.transform.localRotation = Quaternion.identity;
            content.transform.localPosition = Vector3.up * (ComputeBottomOffset(content.transform) + placedObjectSurfacePadding);
        }
        else
        {
            var placementRoot = new GameObject(FallbackPlacementRootName);
            placementRoot.transform.SetPositionAndRotation(placementPose.position, placementPose.rotation);
            content.transform.SetParent(placementRoot.transform, false);
            content.transform.localRotation = Quaternion.identity;
            content.transform.localPosition = Vector3.up * (ComputeBottomOffset(content.transform) + placedObjectSurfacePadding);
        }

        _placedAnchor = anchor;
        _placedContent = anchor != null ? content : content.transform.parent.gameObject;
        UpdateStatus(anchor != null
            ? "Phone-AR prototype anchor created. This is not yet aligned to the DJI camera feed."
            : "Temporary test object created without an AR anchor. This is only for on-device visual verification.");

        _isCreatingAnchor = false;
        RefreshUiState();
    }

    private void ClearPlacedContent()
    {
        if (_placedContent != null)
            Destroy(_placedContent);

        if (_placedAnchor != null)
            Destroy(_placedAnchor.gameObject);

        _placedContent = null;
        _placedAnchor = null;
    }

    private GameObject CreatePlacedContent()
    {
        if (placedObjectPrefab != null)
            return Instantiate(placedObjectPrefab);

        var fallbackCube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        fallbackCube.name = FallbackCubeName;
        fallbackCube.transform.localScale = fallbackCubeScale;

        var renderer = fallbackCube.GetComponent<Renderer>();
        if (renderer != null)
            renderer.sharedMaterial = CreateRuntimeMaterial(new Color(0.98f, 0.78f, 0.18f, 1f));

        return fallbackCube;
    }

    private void UpdateWarningLabel()
    {
        if (warningText != null)
            warningText.text = PrototypeWarningText;
    }

    private void UpdateStatus(string message)
    {
        if (string.Equals(_lastStatusMessage, message, StringComparison.Ordinal))
            return;

        _lastStatusMessage = message;

        if (statusText != null)
            statusText.text = message;

        if (verboseLogs)
            Debug.Log("[AR Prototype] " + message);
    }

    private void UpdateSessionDiagnostics()
    {
        var sessionState = ARSession.state;
        var notTrackingReason = ARSession.notTrackingReason;

        if (sessionState == ARSessionState.SessionTracking)
            _trackingEverEstablished = true;

        if (sessionState == _lastSessionState && notTrackingReason == _lastNotTrackingReason)
            return;

        _lastSessionState = sessionState;
        _lastNotTrackingReason = notTrackingReason;

        if (verboseLogs)
        {
            Debug.Log(
                $"[AR Prototype] Session state changed: {sessionState}" +
                (notTrackingReason != NotTrackingReason.None ? $" ({notTrackingReason})" : string.Empty)
            );
        }

        ApplyCameraFeedPresentation(force: false);

        if (!_hasPlacementPose && !_isCreatingAnchor)
            UpdateStatus(BuildIdleStatusMessage(sessionState, notTrackingReason));
    }

    private string BuildIdleStatusMessage(ARSessionState sessionState, NotTrackingReason notTrackingReason)
    {
        if (ShouldGuideTrackingStartup(sessionState))
            return BuildTrackingStartupMessage(sessionState, notTrackingReason);

        switch (sessionState)
        {
            case ARSessionState.None:
                return "AR session not started yet.";
            case ARSessionState.CheckingAvailability:
                return "Checking ARCore availability on the device.";
            case ARSessionState.NeedsInstall:
                return "ARCore needs to be installed or updated on the device.";
            case ARSessionState.Installing:
                return "Installing or updating ARCore.";
            case ARSessionState.Unsupported:
                return "This device does not report ARCore support.";
            case ARSessionState.Ready:
                return allowPlacementWithoutPlane
                    ? "AR session ready. Move the phone for tracking, or press Place to spawn a temporary test object."
                    : "AR session ready. Move the phone to start detecting a horizontal plane.";
            case ARSessionState.SessionInitializing:
                return "AR session is initializing. Move the phone slowly so tracking can start.";
            case ARSessionState.SessionTracking:
                return allowPlacementWithoutPlane
                    ? "Tracking is live. Move the phone to find a horizontal plane, or press Place for a temporary test object."
                    : "Tracking is live. Move the phone until a horizontal plane is found.";
            default:
                if (notTrackingReason == NotTrackingReason.None)
                    return "Move the phone until AR Foundation finds a horizontal plane.";

                return $"AR tracking is not ready yet: {notTrackingReason}.";
        }
    }

    private void UpdateTrackingStartup()
    {
        var sessionState = ARSession.state;
        ApplyCameraFeedPresentation(force: false);
        UpdateFeatureStartupGates(forceDisableRaycast: ShouldGuideTrackingStartup(sessionState));

        if (sessionState == ARSessionState.SessionTracking)
            return;

        if (!_applicationHasFocus || _applicationPaused)
            return;

        if (_startupRecoveryCoroutine != null)
            return;

        if (_sessionRestartInProgress || maxAutomaticSessionResets <= 0)
            return;

        if (Time.unscaledTime - _currentTrackingAttemptStartedAt < trackingStartupTimeout)
            return;

        if (_automaticSessionResetCount >= maxAutomaticSessionResets)
            return;

        var reason = GetTrackingRestartReason(sessionState);
        if (reason == null)
            return;

        StartCoroutine(RestartArSessionAsync(reason, countTowardsAutomaticLimit: true));
    }

    private string GetTrackingRestartReason(ARSessionState sessionState)
    {
        if (restartSessionOnTimestampRegression && _cameraFrameTimestampRegressed)
            return "camera frame timestamps regressed";

        if (!_hasReceivedCameraFrame)
            return "the camera feed never reached AR Foundation";

        if (sessionState == ARSessionState.SessionInitializing)
            return "the AR session stayed in initialization too long";

        if (ARSession.notTrackingReason != NotTrackingReason.None)
            return $"tracking stayed unavailable ({ARSession.notTrackingReason})";

        return "tracking never became active";
    }

    private IEnumerator RestartArSessionAsync(string reason, bool countTowardsAutomaticLimit)
    {
        _sessionRestartInProgress = true;
        if (countTowardsAutomaticLimit)
            _automaticSessionResetCount++;

        UpdateStatus($"AR tracking got stuck ({reason}). Restarting the AR session once...");

        if (verboseLogs)
            Debug.LogWarning($"[AR Prototype] Restarting AR session after startup failure: {reason}");

        if (arSession != null)
            arSession.enabled = false;

        yield return null;
        yield return new WaitForSecondsRealtime(sessionRestartDelay);

        ResetTrackingAttemptState();
        ApplyTrackingStartupPreferences();
        ApplyCameraFeedPresentation(force: true);

        if (arSession != null)
        {
            arSession.Reset();
            arSession.enabled = true;
        }

        _sessionRestartInProgress = false;
        UpdateFeatureStartupGates(forceDisableRaycast: ShouldGuideTrackingStartup(ARSession.state));
        UpdateStatus("AR session restarted. Point the phone camera at a textured floor or wall and move slowly.");
    }

    private void OnCameraFrameReceived(ARCameraFrameEventArgs eventArgs)
    {
        _hasReceivedCameraFrame = true;
        _lastCameraFrameReceivedAt = Time.unscaledTime;

        if (!eventArgs.timestampNs.HasValue)
            return;

        var timestampNs = eventArgs.timestampNs.Value;
        if (_lastCameraFrameTimestampNs.HasValue && timestampNs <= _lastCameraFrameTimestampNs.Value)
        {
            _cameraFrameTimestampRegressed = true;

            if (verboseLogs)
            {
                Debug.LogWarning(
                    $"[AR Prototype] AR camera frame timestamp regressed: current={timestampNs} previous={_lastCameraFrameTimestampNs.Value}"
                );
            }
        }

        _lastCameraFrameTimestampNs = timestampNs;
    }

    private void BeginTrackingAttempt()
    {
        _trackingEverEstablished = ARSession.state == ARSessionState.SessionTracking;
        _startupWasInterrupted = false;
        ResetTrackingAttemptState();
    }

    private void ResetTrackingAttemptState()
    {
        _currentTrackingAttemptStartedAt = Time.unscaledTime;
        _hasReceivedCameraFrame = false;
        _cameraFrameTimestampRegressed = false;
        _lastCameraFrameTimestampNs = null;
        _lastCameraFrameReceivedAt = Time.unscaledTime;
    }

    private void ApplyCameraFeedPresentation(bool force)
    {
        if (!usePhoneCameraFeedWhileInitializing)
            return;

        var shouldPresentPhoneCameraFeed = ShouldGuideTrackingStartup(ARSession.state);
        if (!force && shouldPresentPhoneCameraFeed == _presentingPhoneCameraFeed)
            return;

        _presentingPhoneCameraFeed = shouldPresentPhoneCameraFeed;

        if (arCameraBackground != null)
            arCameraBackground.enabled = shouldPresentPhoneCameraFeed;

        if (djiGpuBackground != null)
            djiGpuBackground.enabled = !shouldPresentPhoneCameraFeed;

        if (verboseLogs)
        {
            Debug.Log(
                $"[AR Prototype] Camera presentation -> " +
                $"{(shouldPresentPhoneCameraFeed ? "phone AR camera for tracking startup" : "DJI video feed")}"
            );
        }
    }

    private void UpdateFeatureStartupGates(bool forceDisableRaycast)
    {
        if (raycastManager != null)
            raycastManager.enabled = !forceDisableRaycast && !_sessionRestartInProgress;

        if (anchorManager != null)
            anchorManager.enabled = !forceDisableRaycast && !_sessionRestartInProgress;
    }

    private bool ShouldGuideTrackingStartup(ARSessionState sessionState)
    {
        return usePhoneCameraFeedWhileInitializing &&
            !_trackingEverEstablished &&
            sessionState != ARSessionState.SessionTracking;
    }

    private void ApplyTrackingStartupPreferences()
    {
        if (planeManager != null)
            planeManager.requestedDetectionMode = PlaneDetectionMode.Horizontal;

        if (arCameraManager != null)
        {
            arCameraManager.autoFocusRequested = true;
            arCameraManager.requestedLightEstimation = LightEstimation.None;
            arCameraManager.requestedFacingDirection = CameraFacingDirection.World;
        }

        if (arSession != null)
            arSession.matchFrameRateRequested = false;
    }

    private void RegisterStartupInterruption(string reason)
    {
        if (_trackingEverEstablished || !ShouldGuideTrackingStartup(ARSession.state))
            return;

        _startupWasInterrupted = true;
        StopStartupRecoveryRoutine();

        if (verboseLogs)
            Debug.LogWarning($"[AR Prototype] Startup tracking was interrupted because {reason}.");
    }

    private void RecoverTrackingStartupAfterInterruption(string reason)
    {
        if (!restartSessionWhenStartupIsInterrupted || !_startupWasInterrupted || _trackingEverEstablished)
            return;

        if (!isActiveAndEnabled)
            return;

        StopStartupRecoveryRoutine();
        _startupRecoveryCoroutine = StartCoroutine(RecoverTrackingStartupAfterInterruptionAsync(reason));
    }

    private IEnumerator RecoverTrackingStartupAfterInterruptionAsync(string reason)
    {
        if (startupRecoveryDelay > 0f)
            yield return new WaitForSecondsRealtime(startupRecoveryDelay);

        _startupRecoveryCoroutine = null;

        if (!isActiveAndEnabled || _trackingEverEstablished || !_applicationHasFocus || _applicationPaused)
            yield break;

        _startupWasInterrupted = false;

        if (arSession == null)
        {
            BeginTrackingAttempt();
            ApplyTrackingStartupPreferences();
            yield break;
        }

        yield return RestartArSessionAsync($"{reason} during AR startup", countTowardsAutomaticLimit: false);
    }

    private void StopStartupRecoveryRoutine()
    {
        if (_startupRecoveryCoroutine == null)
            return;

        StopCoroutine(_startupRecoveryCoroutine);
        _startupRecoveryCoroutine = null;
    }

    private bool ShouldHoldPlacementUntilTracking()
    {
        return ShouldGuideTrackingStartup(ARSession.state);
    }

    private string BuildTrackingStartupMessage(ARSessionState sessionState, NotTrackingReason notTrackingReason)
    {
        if (_sessionRestartInProgress)
            return "Restarting the AR session. Keep the phone pointed at a textured real-world surface.";

        var baseMessage = sessionState switch
        {
            ARSessionState.CheckingAvailability => "Checking ARCore availability.",
            ARSessionState.NeedsInstall => "ARCore needs to be installed or updated.",
            ARSessionState.Installing => "Installing or updating ARCore.",
            ARSessionState.Unsupported => "This device does not report ARCore support.",
            ARSessionState.Ready => "ARCore is ready. Point the phone camera at a textured floor or wall and move slowly.",
            ARSessionState.SessionInitializing => "Starting AR tracking. Point the phone camera at a textured floor or wall and move slowly.",
            _ => "Point the phone camera at a textured floor or wall and move slowly until tracking starts."
        };

        if (!_hasReceivedCameraFrame && sessionState != ARSessionState.CheckingAvailability)
            return baseMessage + " Waiting for the first AR camera frame.";

        if (notTrackingReason != NotTrackingReason.None)
            return baseMessage + $" Current ARCore state: {notTrackingReason}.";

        return baseMessage;
    }

    private bool TryGetFallbackPlacementPose(out Pose pose)
    {
        pose = default;
        if (!allowPlacementWithoutPlane || targetCamera == null)
            return false;

        var forward = Vector3.ProjectOnPlane(targetCamera.transform.forward, Vector3.up);
        if (forward.sqrMagnitude < 0.0001f)
            forward = targetCamera.transform.forward;

        if (forward.sqrMagnitude < 0.0001f)
            forward = Vector3.forward;

        forward.Normalize();
        pose = new Pose(
            targetCamera.transform.position + forward * fallbackPlacementDistance,
            Quaternion.LookRotation(forward, Vector3.up)
        );
        return true;
    }

    private static float ComputeBottomOffset(Transform root)
    {
        var renderers = root.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
            return 0f;

        var minY = float.PositiveInfinity;
        foreach (var currentRenderer in renderers)
        {
            var bounds = currentRenderer.bounds;
            var extents = bounds.extents;
            var center = bounds.center;

            for (var x = -1; x <= 1; x += 2)
            {
                for (var y = -1; y <= 1; y += 2)
                {
                    for (var z = -1; z <= 1; z += 2)
                    {
                        var worldCorner = center + Vector3.Scale(extents, new Vector3(x, y, z));
                        var localCorner = root.InverseTransformPoint(worldCorner);
                        if (localCorner.y < minY)
                            minY = localCorner.y;
                    }
                }
            }
        }

        return float.IsInfinity(minY) ? 0f : -minY;
    }

    private static T GetOrAddComponent<T>(GameObject gameObject) where T : Component
    {
        var component = gameObject.GetComponent<T>();
        return component != null ? component : gameObject.AddComponent<T>();
    }

    private static Material CreateRuntimeMaterial(Color color)
    {
        var shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null)
            shader = Shader.Find("Unlit/Color");
        if (shader == null)
            shader = Shader.Find("Standard");

        var material = new Material(shader);
        if (material.HasProperty("_BaseColor"))
            material.SetColor("_BaseColor", color);
        else if (material.HasProperty("_Color"))
            material.SetColor("_Color", color);

        return material;
    }

    private static RectTransform CreateEmptyRect(
        string name,
        RectTransform parent,
        Vector2 anchorMin,
        Vector2 anchorMax,
        Vector2 anchoredPosition,
        Vector2 size)
    {
        var gameObject = new GameObject(name, typeof(RectTransform));
        var rect = gameObject.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = size;
        return rect;
    }

    private static RectTransform CreatePanel(
        string name,
        RectTransform parent,
        Vector2 anchorMin,
        Vector2 anchorMax,
        Vector2 anchoredPosition,
        Vector2 size)
    {
        var rect = CreateEmptyRect(name, parent, anchorMin, anchorMax, anchoredPosition, size);
        rect.gameObject.AddComponent<Image>();
        return rect;
    }

    private static Button CreateButton(string name, RectTransform parent, string label, Vector2 anchoredPosition)
    {
        var buttonRect = CreatePanel(name, parent, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), anchoredPosition, new Vector2(180f, 48f));
        var image = buttonRect.GetComponent<Image>();
        if (image != null)
            image.color = new Color(0.13f, 0.3f, 0.56f, 0.92f);

        var button = buttonRect.gameObject.AddComponent<Button>();
        var labelText = CreateText($"{name}Label", buttonRect, 22, TextAnchor.MiddleCenter, Color.white);
        labelText.text = label;
        labelText.rectTransform.offsetMin = Vector2.zero;
        labelText.rectTransform.offsetMax = Vector2.zero;
        return button;
    }

    private static Text CreateText(string name, RectTransform parent, int fontSize, TextAnchor alignment, Color color)
    {
        var textObject = new GameObject(name, typeof(RectTransform));
        var rect = textObject.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        var text = textObject.AddComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.alignment = alignment;
        text.fontSize = fontSize;
        text.color = color;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        return text;
    }

    private static void CreateReticleBar(RectTransform parent, string name, Vector2 size)
    {
        var bar = CreateEmptyRect(name, parent, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, size);
        var image = bar.gameObject.AddComponent<Image>();
        image.color = new Color(0.12f, 1f, 0.72f, 0.95f);
    }
}
