using UnityEngine;
using UnityEngine.UI;

/// <summary>Scene-local presentation controls. Never queries or changes a localization pose.</summary>
[DisallowMultipleComponent]
public sealed class DjiModelPresentationControls : MonoBehaviour
{
    private DjiSharedReferenceCubeOverlay _overlay;
    private Canvas _canvas;
    private RectTransform _safeArea;
    private RectTransform _panel;
    private Button _visibilityButton;
    private Text _visibilityLabel;
    private Rect _lastSafeArea;
    private Vector2Int _lastSize;

    public void Configure(DjiSharedReferenceCubeOverlay overlay)
    {
        _overlay = overlay;
        EnsureUi();
        RefreshUi();
    }

    public void ToggleModelVisibility()
    {
        if (_overlay != null && _overlay.HasRoofCanopyContent)
            _overlay.SetModelVisibleByUser(!_overlay.ModelVisibleByUser);
        RefreshUi();
    }

    private void Awake() => EnsureUi();
    private void OnEnable() { if (_canvas != null) _canvas.enabled = true; }
    private void OnDisable() { if (_canvas != null) _canvas.enabled = false; }

    private void LateUpdate()
    {
        ApplySafeArea(Screen.safeArea, new Vector2Int(Screen.width, Screen.height));
        RefreshUi();
    }

    private void RefreshUi()
    {
        if (_visibilityButton == null)
            return;
        _visibilityButton.interactable = _overlay != null && _overlay.HasRoofCanopyContent;
        string label = _overlay == null || _overlay.ModelVisibleByUser ? "HIDE MODEL" : "SHOW MODEL";
        if (_visibilityLabel.text != label)
            _visibilityLabel.text = label;
    }

    private void EnsureUi()
    {
        if (_canvas != null)
            return;
        var root = new GameObject("DJI Presentation Canvas", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
        root.transform.SetParent(transform, false);
        _canvas = root.GetComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.overrideSorting = true;
        _canvas.sortingOrder = 3001;
        var safe = new GameObject("Safe Area", typeof(RectTransform));
        safe.transform.SetParent(root.transform, false);
        _safeArea = safe.GetComponent<RectTransform>();
        var panel = new GameObject("Presentation Controls", typeof(RectTransform));
        panel.transform.SetParent(_safeArea, false);
        _panel = panel.GetComponent<RectTransform>();
        _panel.anchorMin = _panel.anchorMax = _panel.pivot = Vector2.right;
        _panel.anchoredPosition = new Vector2(-24f, 24f);
        _panel.sizeDelta = new Vector2(146f, 48f);
        _visibilityButton = CreateButton("Model Visibility", "HIDE MODEL", out _visibilityLabel);
        _visibilityButton.onClick.AddListener(ToggleModelVisibility);
        ApplySafeArea(Screen.safeArea, new Vector2Int(Screen.width, Screen.height));
        RefreshUi();
    }

    private Button CreateButton(string name, string text, out Text label)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(_panel, false);
        var buttonRect = go.GetComponent<RectTransform>();
        buttonRect.anchorMin = Vector2.zero;
        buttonRect.anchorMax = Vector2.one;
        buttonRect.offsetMin = buttonRect.offsetMax = Vector2.zero;
        go.GetComponent<Image>().color = new Color(.015f, .07f, .1f, .88f);
        var button = go.GetComponent<Button>();
        button.targetGraphic = go.GetComponent<Image>();
        var colors = button.colors;
        colors.disabledColor = new Color(.6f, .6f, .6f, .65f);
        button.colors = colors;
        var textObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
        textObject.transform.SetParent(go.transform, false);
        var rect = textObject.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(6f, 0f);
        rect.offsetMax = new Vector2(-6f, 0f);
        label = textObject.GetComponent<Text>();
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.fontSize = 18;
        label.fontStyle = FontStyle.Bold;
        label.color = Color.white;
        label.text = text;
        label.alignment = TextAnchor.MiddleCenter;
        label.raycastTarget = false;
        return button;
    }

    private void ApplySafeArea(Rect area, Vector2Int size)
    {
        if (size.x <= 0 || size.y <= 0 || _safeArea == null || (area == _lastSafeArea && size == _lastSize))
            return;
        _lastSafeArea = area;
        _lastSize = size;
        _safeArea.anchorMin = new Vector2(area.xMin / size.x, area.yMin / size.y);
        _safeArea.anchorMax = new Vector2(area.xMax / size.x, area.yMax / size.y);
        _safeArea.offsetMin = _safeArea.offsetMax = Vector2.zero;
        _panel.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, Mathf.Min(146f, Mathf.Max(0f, area.width - 48f)));
    }
}
