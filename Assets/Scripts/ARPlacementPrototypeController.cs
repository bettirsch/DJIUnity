using System;
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

    [Header("Scene References")]
    [SerializeField] private Camera targetCamera;
    [SerializeField] private XROrigin xrOrigin;
    [SerializeField] private ARSession arSession;
    [SerializeField] private ARPlaneManager planeManager;
    [SerializeField] private ARRaycastManager raycastManager;
    [SerializeField] private ARAnchorManager anchorManager;
    [SerializeField] private ARCameraManager arCameraManager;

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
    [SerializeField] private bool preferPlaneAttachedAnchorWhenAvailable;
    [SerializeField] [Min(0f)] private float indicatorSurfaceOffset = 0.01f;
    [SerializeField] [Min(0f)] private float placedObjectSurfacePadding = 0.01f;
    [SerializeField] private Vector3 fallbackCubeScale = new Vector3(0.25f, 0.25f, 0.25f);

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
    private bool _isCreatingAnchor;
    private bool _loggedMissingPlacement;

    private void Awake()
    {
        ResolveReferences();

        if (autoCreateSceneDependencies)
            EnsureSceneDependencies();

        DisableConflictingPrototypeComponents();
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
    }

    private void OnDisable()
    {
        if (placeButton != null)
            placeButton.onClick.RemoveListener(OnPlaceButtonPressed);

        if (resetButton != null)
            resetButton.onClick.RemoveListener(OnResetButtonPressed);
    }

    private void Update()
    {
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
        if (targetCamera == null || raycastManager == null)
        {
            SetPlacementAvailability(false, default, null);
            return;
        }

        var screenPoint = targetCamera.ViewportToScreenPoint(new Vector3(_viewportCenter.x, _viewportCenter.y, 0f));
        if (!raycastManager.Raycast(screenPoint, _raycastHits, TrackableType.PlaneWithinPolygon))
        {
            if (!_loggedMissingPlacement && verboseLogs)
            {
                _loggedMissingPlacement = true;
                Debug.Log("[AR Prototype] No horizontal plane hit at screen centre.");
            }

            SetPlacementAvailability(false, default, null);
            return;
        }

        _loggedMissingPlacement = false;
        var hit = _raycastHits[0];
        var plane = planeManager != null ? planeManager.GetPlane(hit.trackableId) : null;
        SetPlacementAvailability(true, hit.pose, plane);
    }

    private void SetPlacementAvailability(bool available, Pose pose, ARPlane plane)
    {
        _hasPlacementPose = available;
        _currentPlacementPose = pose;
        _currentPlacementPlane = plane;

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
            UpdateStatus(_placedAnchor == null
                ? "Horizontal plane found. Press Place to create a phone-AR test anchor."
                : "Horizontal plane found. Press Place to replace the current test anchor.");
        }
        else if (!_isCreatingAnchor)
        {
            UpdateStatus(_placedAnchor == null
                ? "Move the phone until AR Foundation finds a horizontal plane."
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
            UpdateStatus("No valid plane hit is available for placement.");
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
        UpdateStatus("Creating anchor...");

        ClearPlacedContent();

        ARAnchor anchor = null;
        if (preferPlaneAttachedAnchorWhenAvailable && placementPlane != null)
        {
            anchor = anchorManager.AttachAnchor(placementPlane, placementPose);
            if (anchor == null && verboseLogs)
                Debug.LogWarning("[AR Prototype] Plane-attached anchor failed, falling back to TryAddAnchorAsync.");
        }

        if (anchor == null)
        {
            if (anchorManager == null)
            {
                Debug.LogError("[AR Prototype] ARAnchorManager is missing.");
                UpdateStatus("ARAnchorManager is missing. Cannot create an anchor.");
                _isCreatingAnchor = false;
                RefreshUiState();
                return;
            }

            try
            {
                var result = await anchorManager.TryAddAnchorAsync(placementPose);
                if (!result.status.IsSuccess() || result.value == null)
                {
                    Debug.LogError($"[AR Prototype] TryAddAnchorAsync failed: {result.status}");
                    UpdateStatus($"Anchor creation failed: {result.status}");
                    _isCreatingAnchor = false;
                    RefreshUiState();
                    return;
                }

                anchor = result.value;
            }
            catch (Exception exception)
            {
                Debug.LogError("[AR Prototype] Exception while creating anchor: " + exception);
                UpdateStatus("Anchor creation threw an exception. See Console for details.");
                _isCreatingAnchor = false;
                RefreshUiState();
                return;
            }
        }

        var content = CreatePlacedContent();
        if (content == null)
        {
            Debug.LogError("[AR Prototype] Unable to create placement content.");
            Destroy(anchor.gameObject);
            UpdateStatus("Content creation failed. The anchor was removed.");
            _isCreatingAnchor = false;
            RefreshUiState();
            return;
        }

        content.transform.SetParent(anchor.transform, false);
        content.transform.localRotation = Quaternion.identity;
        content.transform.localPosition = Vector3.up * (ComputeBottomOffset(content.transform) + placedObjectSurfacePadding);

        _placedAnchor = anchor;
        _placedContent = content;
        UpdateStatus("Phone-AR prototype anchor created. This is not yet aligned to the DJI camera feed.");

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
        if (statusText != null)
            statusText.text = message;

        if (verboseLogs)
            Debug.Log("[AR Prototype] " + message);
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
