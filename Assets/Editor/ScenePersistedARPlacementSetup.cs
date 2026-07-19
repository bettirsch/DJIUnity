using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.InputSystem.XR;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.Management;

public static class ScenePersistedARPlacementSetup
{
    private const string SampleScenePath = "Assets/Scenes/SampleScene.unity";
    private const string XrOriginName = "XR Origin";
    private const string CameraOffsetName = "Camera Offset";
    private const string ArSessionName = "AR Session";
    private const string EventSystemName = "AR Placement EventSystem";
    private const string CanvasName = "AR Placement Canvas";
    private const string WarningPanelName = "Prototype Warning";
    private const string StatusPanelName = "Placement Status";
    private const string ButtonRowName = "Placement Buttons";
    private const string PlaceButtonName = "PlaceButton";
    private const string ResetButtonName = "ResetButton";
    private const string CenterReticleName = "Center Reticle";
    private const string IndicatorName = "AR Placement Indicator";

    [MenuItem("Tools/DJI/Apply Scene-Persisted AR Placement Setup")]
    public static void ApplyToSampleScene()
    {
        var scene = EditorSceneManager.OpenScene(SampleScenePath, OpenSceneMode.Single);
        ConfigureScene();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log("[AR Prototype] Scene-persisted XR Origin + UI wiring saved to SampleScene.");
    }

    public static void ConfigureScene()
    {
        var mainCamera = FindMainCamera();
        if (mainCamera == null)
            throw new MissingReferenceException("SampleScene does not contain a camera to wire into the XR Origin.");

        var xrOrigin = ConfigureXrOrigin(mainCamera);
        var arSession = ConfigureArSession();
        ConfigureXrManagement();
        var arCameraManager = GetOrAddComponent<ARCameraManager>(mainCamera.gameObject);
        var trackedPoseDriver = GetOrAddComponent<TrackedPoseDriver>(mainCamera.gameObject);
        ConfigureTrackedPoseDriver(trackedPoseDriver);

        var planeManager = GetOrAddComponent<ARPlaneManager>(xrOrigin.gameObject);
        planeManager.requestedDetectionMode = PlaneDetectionMode.Horizontal;

        var raycastManager = GetOrAddComponent<ARRaycastManager>(xrOrigin.gameObject);
        var anchorManager = GetOrAddComponent<ARAnchorManager>(xrOrigin.gameObject);
        var controller = GetOrAddComponent<ARPlacementPrototypeController>(xrOrigin.gameObject);

        DisableLegacyPrototypeObjects(mainCamera);

        var overlayCanvas = ConfigureCanvas();
        ConfigureEventSystem();

        var warningText = ConfigureWarningPanel(overlayCanvas);
        var statusText = ConfigureStatusPanel(overlayCanvas);
        var placeButton = ConfigureButton(overlayCanvas, PlaceButtonName, "Place", new Vector2(-110f, 0f));
        var resetButton = ConfigureButton(overlayCanvas, ResetButtonName, "Reset", new Vector2(110f, 0f));
        var centerReticle = ConfigureReticle(overlayCanvas);
        var indicator = ConfigureIndicator(xrOrigin.transform);

        ConfigureController(
            controller,
            mainCamera,
            xrOrigin,
            arSession,
            planeManager,
            raycastManager,
            anchorManager,
            arCameraManager,
            overlayCanvas,
            placeButton,
            resetButton,
            warningText,
            statusText,
            centerReticle,
            indicator
        );

        EditorUtility.SetDirty(mainCamera.gameObject);
        EditorUtility.SetDirty(xrOrigin.gameObject);
        EditorUtility.SetDirty(arSession.gameObject);
        EditorUtility.SetDirty(overlayCanvas.gameObject);
    }

    private static Camera FindMainCamera()
    {
        var taggedMainCamera = Camera.main;
        if (taggedMainCamera != null)
            return taggedMainCamera;

        return Object.FindAnyObjectByType<Camera>();
    }

    private static XROrigin ConfigureXrOrigin(Camera mainCamera)
    {
        var xrOriginObject = FindOrCreateRoot(XrOriginName);
        var xrOrigin = GetOrAddComponent<XROrigin>(xrOriginObject);
        var cameraOffset = FindOrCreateChild(xrOriginObject.transform, CameraOffsetName);

        if (mainCamera.transform.parent != cameraOffset.transform)
            mainCamera.transform.SetParent(cameraOffset.transform, true);

        mainCamera.transform.localPosition = Vector3.zero;
        mainCamera.transform.localRotation = Quaternion.identity;

        xrOrigin.Origin = xrOriginObject;
        xrOrigin.CameraFloorOffsetObject = cameraOffset;
        xrOrigin.Camera = mainCamera;
        xrOrigin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Device;
        xrOrigin.CameraYOffset = 0f;
        return xrOrigin;
    }

    private static ARSession ConfigureArSession()
    {
        var sessionObject = FindOrCreateRoot(ArSessionName);
        var session = GetOrAddComponent<ARSession>(sessionObject);
        GetOrAddComponent<ARInputManager>(sessionObject);
        return session;
    }

    private static void ConfigureXrManagement()
    {
        const string ArCoreLoaderType = "UnityEngine.XR.ARCore.ARCoreLoader";
        const string XrSettingsAssetPath = "Assets/XR/XRGeneralSettingsPerBuildTarget.asset";

        EnsureFolder("Assets/XR");
        EnsureFolder("Assets/XR/Loaders");

        if (!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out XRGeneralSettingsPerBuildTarget buildTargetSettings) ||
            buildTargetSettings == null)
        {
            buildTargetSettings = AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>(XrSettingsAssetPath);
            if (buildTargetSettings == null)
            {
                buildTargetSettings = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(buildTargetSettings, XrSettingsAssetPath);
            }

            EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, buildTargetSettings, true);
        }

        if (!buildTargetSettings.HasSettingsForBuildTarget(BuildTargetGroup.Android))
            buildTargetSettings.CreateDefaultSettingsForBuildTarget(BuildTargetGroup.Android);

        if (!buildTargetSettings.HasManagerSettingsForBuildTarget(BuildTargetGroup.Android))
            buildTargetSettings.CreateDefaultManagerSettingsForBuildTarget(BuildTargetGroup.Android);

        var generalSettings = buildTargetSettings.SettingsForBuildTarget(BuildTargetGroup.Android);
        generalSettings.InitManagerOnStart = true;

        var managerSettings = buildTargetSettings.ManagerSettingsForBuildTarget(BuildTargetGroup.Android);
        managerSettings.automaticLoading = true;
        managerSettings.automaticRunning = true;

        if (!XRPackageMetadataStore.IsLoaderAssigned(ArCoreLoaderType, BuildTargetGroup.Android))
            XRPackageMetadataStore.AssignLoader(managerSettings, ArCoreLoaderType, BuildTargetGroup.Android);

        EditorUtility.SetDirty(buildTargetSettings);
        EditorUtility.SetDirty(generalSettings);
        EditorUtility.SetDirty(managerSettings);
        AssetDatabase.SaveAssets();
    }

    private static void EnsureFolder(string assetPath)
    {
        if (AssetDatabase.IsValidFolder(assetPath))
            return;

        var parentPath = System.IO.Path.GetDirectoryName(assetPath)?.Replace("\\", "/");
        var folderName = System.IO.Path.GetFileName(assetPath);

        if (!string.IsNullOrEmpty(parentPath) && !AssetDatabase.IsValidFolder(parentPath))
            EnsureFolder(parentPath);

        AssetDatabase.CreateFolder(parentPath ?? "Assets", folderName);
    }

    private static void ConfigureTrackedPoseDriver(TrackedPoseDriver trackedPoseDriver)
    {
        var positionAction = new InputAction("Position", binding: "<XRHMD>/centerEyePosition", expectedControlType: "Vector3");
        positionAction.AddBinding("<HandheldARInputDevice>/devicePosition");

        var rotationAction = new InputAction("Rotation", binding: "<XRHMD>/centerEyeRotation", expectedControlType: "Quaternion");
        rotationAction.AddBinding("<HandheldARInputDevice>/deviceRotation");

        trackedPoseDriver.positionInput = new InputActionProperty(positionAction);
        trackedPoseDriver.rotationInput = new InputActionProperty(rotationAction);
    }

    private static void DisableLegacyPrototypeObjects(Camera mainCamera)
    {
        foreach (var marker in Object.FindObjectsByType<TapToPlaceMarker>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            marker.enabled = false;

            foreach (var currentRenderer in marker.GetComponentsInChildren<Renderer>(true))
            {
                currentRenderer.enabled = false;
                EditorUtility.SetDirty(currentRenderer);
            }

            EditorUtility.SetDirty(marker);
        }

        var poseDriver = mainCamera.GetComponent<DJICameraPoseDriver>();
        if (poseDriver != null)
        {
            poseDriver.enabled = false;
            EditorUtility.SetDirty(poseDriver);
        }
    }

    private static Canvas ConfigureCanvas()
    {
        var canvasObject = FindOrCreateRoot(CanvasName);
        var canvas = GetOrAddComponent<Canvas>(canvasObject);
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 1000;

        var scaler = GetOrAddComponent<CanvasScaler>(canvasObject);
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);

        GetOrAddComponent<GraphicRaycaster>(canvasObject);
        return canvas;
    }

    private static void ConfigureEventSystem()
    {
        var eventSystemObject = FindOrCreateRoot(EventSystemName);
        GetOrAddComponent<EventSystem>(eventSystemObject);
        GetOrAddComponent<InputSystemUIInputModule>(eventSystemObject);
    }

    private static Text ConfigureWarningPanel(Canvas canvas)
    {
        var panel = FindOrCreatePanel(
            canvas.transform as RectTransform,
            WarningPanelName,
            new Vector2(0.5f, 1f),
            new Vector2(0.5f, 1f),
            new Vector2(0f, -20f),
            new Vector2(980f, 120f),
            new Color(0.76f, 0.24f, 0.16f, 0.88f)
        );

        var text = FindOrCreateText(panel, "WarningText", 24, TextAnchor.MiddleCenter, Color.white);
        text.rectTransform.offsetMin = new Vector2(18f, 18f);
        text.rectTransform.offsetMax = new Vector2(-18f, -18f);
        return text;
    }

    private static Text ConfigureStatusPanel(Canvas canvas)
    {
        var panel = FindOrCreatePanel(
            canvas.transform as RectTransform,
            StatusPanelName,
            new Vector2(0.5f, 0f),
            new Vector2(0.5f, 0f),
            new Vector2(0f, 72f),
            new Vector2(760f, 70f),
            new Color(0.08f, 0.1f, 0.14f, 0.78f)
        );

        var text = FindOrCreateText(panel, "StatusText", 20, TextAnchor.MiddleCenter, Color.white);
        text.rectTransform.offsetMin = new Vector2(12f, 8f);
        text.rectTransform.offsetMax = new Vector2(-12f, -8f);
        return text;
    }

    private static Button ConfigureButton(Canvas canvas, string name, string label, Vector2 anchoredPosition)
    {
        var row = FindOrCreateRect(
            canvas.transform as RectTransform,
            ButtonRowName,
            new Vector2(0.5f, 0f),
            new Vector2(0.5f, 0f),
            new Vector2(0f, 18f),
            new Vector2(520f, 48f)
        );

        var buttonRect = FindOrCreatePanel(
            row,
            name,
            new Vector2(0.5f, 0.5f),
            new Vector2(0.5f, 0.5f),
            anchoredPosition,
            new Vector2(180f, 48f),
            new Color(0.13f, 0.3f, 0.56f, 0.92f)
        );

        var button = GetOrAddComponent<Button>(buttonRect.gameObject);
        var text = FindOrCreateText(buttonRect, $"{name}Label", 22, TextAnchor.MiddleCenter, Color.white);
        text.text = label;
        return button;
    }

    private static RectTransform ConfigureReticle(Canvas canvas)
    {
        var reticle = FindOrCreateRect(
            canvas.transform as RectTransform,
            CenterReticleName,
            new Vector2(0.5f, 0.5f),
            new Vector2(0.5f, 0.5f),
            Vector2.zero,
            new Vector2(56f, 56f)
        );

        ConfigureReticleBar(reticle, "ReticleHorizontal", new Vector2(26f, 3f));
        ConfigureReticleBar(reticle, "ReticleVertical", new Vector2(3f, 26f));
        return reticle;
    }

    private static GameObject ConfigureIndicator(Transform parent)
    {
        var indicator = FindOrCreateChild(parent, IndicatorName);
        indicator.SetActive(false);

        if (indicator.GetComponent<MeshFilter>() == null || indicator.GetComponent<MeshRenderer>() == null)
        {
            var primitive = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            primitive.name = IndicatorName;
            primitive.transform.SetParent(parent, false);

            CopyPrimitiveComponent<MeshFilter>(primitive, indicator);
            CopyPrimitiveComponent<MeshRenderer>(primitive, indicator);

            Object.DestroyImmediate(primitive);
        }

        var collider = indicator.GetComponent<Collider>();
        if (collider != null)
            Object.DestroyImmediate(collider);

        indicator.transform.localScale = new Vector3(0.16f, 0.004f, 0.16f);
        return indicator;
    }

    private static void ConfigureController(
        ARPlacementPrototypeController controller,
        Camera targetCamera,
        XROrigin xrOrigin,
        ARSession arSession,
        ARPlaneManager planeManager,
        ARRaycastManager raycastManager,
        ARAnchorManager anchorManager,
        ARCameraManager arCameraManager,
        Canvas overlayCanvas,
        Button placeButton,
        Button resetButton,
        Text warningText,
        Text statusText,
        RectTransform centerReticle,
        GameObject indicator
    )
    {
        var serializedObject = new SerializedObject(controller);
        serializedObject.FindProperty("targetCamera").objectReferenceValue = targetCamera;
        serializedObject.FindProperty("xrOrigin").objectReferenceValue = xrOrigin;
        serializedObject.FindProperty("arSession").objectReferenceValue = arSession;
        serializedObject.FindProperty("planeManager").objectReferenceValue = planeManager;
        serializedObject.FindProperty("raycastManager").objectReferenceValue = raycastManager;
        serializedObject.FindProperty("anchorManager").objectReferenceValue = anchorManager;
        serializedObject.FindProperty("arCameraManager").objectReferenceValue = arCameraManager;
        serializedObject.FindProperty("overlayCanvas").objectReferenceValue = overlayCanvas;
        serializedObject.FindProperty("placeButton").objectReferenceValue = placeButton;
        serializedObject.FindProperty("resetButton").objectReferenceValue = resetButton;
        serializedObject.FindProperty("warningText").objectReferenceValue = warningText;
        serializedObject.FindProperty("statusText").objectReferenceValue = statusText;
        serializedObject.FindProperty("centerReticle").objectReferenceValue = centerReticle;
        serializedObject.FindProperty("placementIndicatorObject").objectReferenceValue = indicator;
        serializedObject.FindProperty("autoCreateSceneDependencies").boolValue = false;
        serializedObject.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(controller);
    }

    private static void ConfigureReticleBar(RectTransform parent, string name, Vector2 size)
    {
        var bar = FindOrCreateRect(parent, name, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, size);
        var image = GetOrAddComponent<Image>(bar.gameObject);
        image.color = new Color(0.12f, 1f, 0.72f, 0.95f);
    }

    private static RectTransform FindOrCreatePanel(
        RectTransform parent,
        string name,
        Vector2 anchorMin,
        Vector2 anchorMax,
        Vector2 anchoredPosition,
        Vector2 size,
        Color color
    )
    {
        var panel = FindOrCreateRect(parent, name, anchorMin, anchorMax, anchoredPosition, size);
        var image = GetOrAddComponent<Image>(panel.gameObject);
        image.color = color;
        return panel;
    }

    private static RectTransform FindOrCreateRect(
        RectTransform parent,
        string name,
        Vector2 anchorMin,
        Vector2 anchorMax,
        Vector2 anchoredPosition,
        Vector2 size
    )
    {
        var existing = parent.Find(name) as RectTransform;
        if (existing != null)
        {
            ConfigureRect(existing, anchorMin, anchorMax, anchoredPosition, size);
            return existing;
        }

        var gameObject = new GameObject(name, typeof(RectTransform));
        var rect = gameObject.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        ConfigureRect(rect, anchorMin, anchorMax, anchoredPosition, size);
        return rect;
    }

    private static Text FindOrCreateText(RectTransform parent, string name, int fontSize, TextAnchor alignment, Color color)
    {
        var existing = parent.Find(name) as RectTransform;
        var textObject = existing != null ? existing.gameObject : new GameObject(name, typeof(RectTransform));
        var rect = textObject.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        var text = GetOrAddComponent<Text>(textObject);
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.alignment = alignment;
        text.fontSize = fontSize;
        text.color = color;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        return text;
    }

    private static void ConfigureRect(
        RectTransform rect,
        Vector2 anchorMin,
        Vector2 anchorMax,
        Vector2 anchoredPosition,
        Vector2 size
    )
    {
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = size;
    }

    private static GameObject FindOrCreateRoot(string name)
    {
        var existing = GameObject.Find(name);
        return existing != null ? existing : new GameObject(name);
    }

    private static GameObject FindOrCreateChild(Transform parent, string name)
    {
        var existing = parent.Find(name);
        if (existing != null)
            return existing.gameObject;

        var gameObject = new GameObject(name);
        gameObject.transform.SetParent(parent, false);
        return gameObject;
    }

    private static T GetOrAddComponent<T>(GameObject gameObject) where T : Component
    {
        var component = gameObject.GetComponent<T>();
        return component != null ? component : gameObject.AddComponent<T>();
    }

    private static void CopyPrimitiveComponent<T>(GameObject source, GameObject destination) where T : Component
    {
        var sourceComponent = source.GetComponent<T>();
        if (sourceComponent == null)
            return;

        var destinationComponent = destination.GetComponent<T>();
        if (destinationComponent == null)
        {
            destinationComponent = destination.AddComponent<T>();
        }

        EditorUtility.CopySerialized(sourceComponent, destinationComponent);
    }
}
