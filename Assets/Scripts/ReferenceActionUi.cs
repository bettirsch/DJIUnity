using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
#endif

/// <summary>
/// Owns the small action-only Canvas used after a reference board is acquired.
/// Status and diagnostic overlays intentionally remain on a separate Canvas.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Canvas))]
[RequireComponent(typeof(CanvasScaler))]
[RequireComponent(typeof(GraphicRaycaster))]
public sealed class ReferenceActionUi : MonoBehaviour
{
    [SerializeField] private bool pointerRaycastDiagnostics = true;

    private Button _connectDroneButton;
    private Button _rescanReferenceButton;
    private bool _isInitialized;

    public static ReferenceActionUi FindOrCreate()
    {
        var existing = FindFirstObjectByType<ReferenceActionUi>(FindObjectsInactive.Include);
        if (existing != null)
            return existing;

        var actionCanvas = new GameObject("ReferenceActionCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(ReferenceActionUi));
        return actionCanvas.GetComponent<ReferenceActionUi>();
    }

    private void Awake()
    {
        Initialize();
    }

    private void Update()
    {
        LogPointerRaycastAtPointerDown();
    }

    public void Configure(Action connectDroneAction, Action rescanAction)
    {
        Initialize();

        _connectDroneButton.onClick.RemoveAllListeners();
        _connectDroneButton.onClick.AddListener(() => connectDroneAction?.Invoke());
        _rescanReferenceButton.onClick.RemoveAllListeners();
        _rescanReferenceButton.onClick.AddListener(() => rescanAction?.Invoke());
    }

    public void SetConnectDroneVisible(bool visible)
    {
        Initialize();
        _connectDroneButton.gameObject.SetActive(visible);
    }

    public void SetRescanReferenceVisible(bool visible)
    {
        Initialize();
        _rescanReferenceButton.gameObject.SetActive(visible);
    }

    public void LogConfiguration(string phase)
    {
        Initialize();
        Canvas.ForceUpdateCanvases();
        Debug.Log($"[Reference Action UI] UI_CANVAS phase={phase} active={gameObject.activeInHierarchy} renderMode={GetComponent<Canvas>().renderMode} raycasterCount={GetComponents<GraphicRaycaster>().Length}");
        LogButton(phase, _connectDroneButton);
        LogButton(phase, _rescanReferenceButton);
    }

    private void Initialize()
    {
        if (_isInitialized)
            return;

        gameObject.name = "ReferenceActionCanvas";
        ConfigureCanvas();
        EnsureSingleEventSystem();
        BuildActionHierarchy();
        _isInitialized = true;
        _connectDroneButton.gameObject.SetActive(false);
        _rescanReferenceButton.gameObject.SetActive(false);
    }

    private void ConfigureCanvas()
    {
        var canvas = GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.overrideSorting = true;
        canvas.sortingOrder = 1100;

        var scaler = GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        // The Android player is locked to landscape, so use a matching reference resolution.
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        var raycasters = GetComponents<GraphicRaycaster>();
        for (var index = 1; index < raycasters.Length; index++)
        {
            raycasters[index].enabled = false;
            Destroy(raycasters[index]);
        }
    }

    private void EnsureSingleEventSystem()
    {
        var eventSystems = FindObjectsByType<EventSystem>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        var eventSystem = eventSystems.Length > 0 ? eventSystems[0] : null;
        if (eventSystem == null)
        {
            var eventSystemObject = new GameObject("AR Scan EventSystem", typeof(EventSystem));
            eventSystem = eventSystemObject.GetComponent<EventSystem>();
        }

        for (var index = 0; index < eventSystems.Length; index++)
        {
            if (eventSystems[index] != eventSystem)
                eventSystems[index].gameObject.SetActive(false);
        }

#if ENABLE_INPUT_SYSTEM
        var inputModule = eventSystem.GetComponent<InputSystemUIInputModule>();
        if (inputModule == null)
            inputModule = eventSystem.gameObject.AddComponent<InputSystemUIInputModule>();

        foreach (var module in eventSystem.GetComponents<BaseInputModule>())
        {
            if (module == inputModule)
                continue;

            module.enabled = false;
            Destroy(module);
        }

        inputModule.enabled = false;
        inputModule.AssignDefaultActions();
        inputModule.enabled = true;
#endif

        var activeEventSystemCount = FindObjectsByType<EventSystem>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Length;
        Debug.Log($"[Reference Action UI] EVENT_SYSTEM_COUNT={activeEventSystemCount}");
        Debug.Log($"[Reference Action UI] EVENT_SYSTEM={eventSystem.gameObject.name}");
        Debug.Log($"[Reference Action UI] INPUT_MODULE={eventSystem.currentInputModule?.GetType().Name}");
    }

    private void BuildActionHierarchy()
    {
        var panel = new GameObject("ActionPanel", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        panel.transform.SetParent(transform, false);
        var panelRect = panel.GetComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(0.5f, 0f);
        panelRect.anchorMax = new Vector2(0.5f, 0f);
        panelRect.pivot = new Vector2(0.5f, 0f);
        panelRect.anchoredPosition = new Vector2(0f, 48f);
        panelRect.sizeDelta = new Vector2(920f, 0f);

        var layout = panel.GetComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(0, 0, 0, 0);
        layout.spacing = 18f;
        layout.childAlignment = TextAnchor.LowerCenter;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        panel.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        _connectDroneButton = CreateButton(panel.transform, "ConnectDroneButton", "Csatlakoztassa a drónt", new Color(0.05f, 0.43f, 0.33f, 0.96f));
        _rescanReferenceButton = CreateButton(panel.transform, "RescanReferenceButton", "Új keresés", new Color(0.10f, 0.25f, 0.48f, 0.96f));
    }

    private static Button CreateButton(Transform parent, string objectName, string labelText, Color color)
    {
        var buttonObject = new GameObject(objectName, typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        buttonObject.transform.SetParent(parent, false);

        var image = buttonObject.GetComponent<Image>();
        image.color = color;
        image.raycastTarget = true;

        var button = buttonObject.GetComponent<Button>();
        button.targetGraphic = image;
        button.interactable = true;

        var layout = buttonObject.GetComponent<LayoutElement>();
        layout.preferredHeight = 132f;
        layout.minHeight = 132f;

        var textObject = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        textObject.transform.SetParent(buttonObject.transform, false);
        var textRect = textObject.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        var text = textObject.GetComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = 38;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = Color.white;
        text.text = labelText;
        text.raycastTarget = false;
        return button;
    }

    private void LogPointerRaycastAtPointerDown()
    {
#if ENABLE_INPUT_SYSTEM
        if (!pointerRaycastDiagnostics || EventSystem.current == null || Pointer.current == null || !Pointer.current.press.wasPressedThisFrame)
            return;

        var pointerPosition = Pointer.current.position.ReadValue();
        var pointerData = new PointerEventData(EventSystem.current) { position = pointerPosition };
        var results = new List<RaycastResult>();
        EventSystem.current.RaycastAll(pointerData, results);
        Debug.Log($"[Reference Action UI] UI_POINTER_DOWN x={pointerPosition.x:F0} y={pointerPosition.y:F0}");
        Debug.Log($"[Reference Action UI] UI_RAYCAST_COUNT={results.Count}");
        for (var index = 0; index < results.Count; index++)
            Debug.Log($"[Reference Action UI] UI_RAYCAST[{index}]={results[index].gameObject.name}");
#endif
    }

    private static void LogButton(string phase, Button button)
    {
        var rect = button.transform as RectTransform;
        Debug.Log($"[Reference Action UI] UI_BUTTON phase={phase} name={button.name} active={button.gameObject.activeInHierarchy} interactable={button.interactable} rect={rect.rect} targetRaycast={button.targetGraphic.raycastTarget}");
    }
}
