using UnityEngine;
using UnityEngine.UI;

/// <summary>Manual timestamp cues only; never sends aircraft commands.</summary>
internal sealed class DjiMotionMarkerHud : MonoBehaviour
{
    private DjiTelemetryValidationLogger _logger;
    private Canvas _canvas;
    private RectTransform _safeArea;
    private Rect _lastSafeArea;
    private Vector2Int _lastSize;

    public void Configure(DjiTelemetryValidationLogger logger)
    {
        _logger = logger;
        if (_canvas != null) return;
        var root = new GameObject("Motion Marker Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        root.transform.SetParent(transform, false);
        _canvas = root.GetComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.overrideSorting = true;
        _canvas.sortingOrder = 3002;
        var scaler = root.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        var safe = new GameObject("Safe Area", typeof(RectTransform));
        safe.transform.SetParent(root.transform, false);
        _safeArea = safe.GetComponent<RectTransform>();
        AddButton("MARK MOVE START", "MOVE_START", -160f, 190f);
        AddButton("MARK STOP", "STOP", -24f, 128f);
        UpdateSafeArea();
    }

    private void AddButton(string title, string marker, float right, float width)
    {
        var go = new GameObject(title, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(_safeArea, false);
        var rect = go.GetComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1f, 0f);
        rect.anchoredPosition = new Vector2(right, 20f);
        rect.sizeDelta = new Vector2(width, 48f);
        go.GetComponent<Image>().color = new Color(0.06f, 0.09f, 0.12f, 0.8f);
        go.GetComponent<Button>().onClick.AddListener(() => _logger?.RecordUserMotionMarker(marker));
        var label = new GameObject("Label", typeof(RectTransform), typeof(Text));
        label.transform.SetParent(go.transform, false);
        var textRect = label.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.sizeDelta = Vector2.zero;
        var text = label.GetComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = 17;
        text.color = Color.white;
        text.alignment = TextAnchor.MiddleCenter;
        text.raycastTarget = false;
        text.text = title;
    }

    private void OnEnable() { if (_canvas != null) _canvas.enabled = true; }
    private void OnDisable() { if (_canvas != null) _canvas.enabled = false; }
    private void Update() => UpdateSafeArea();
    private void UpdateSafeArea()
    {
        if (_safeArea == null || Screen.width <= 0 || Screen.height <= 0) return;
        var area = Screen.safeArea;
        var size = new Vector2Int(Screen.width, Screen.height);
        if (area == _lastSafeArea && size == _lastSize) return;
        _lastSafeArea = area;
        _lastSize = size;
        _safeArea.anchorMin = new Vector2(area.xMin / size.x, area.yMin / size.y);
        _safeArea.anchorMax = new Vector2(area.xMax / size.x, area.yMax / size.y);
        _safeArea.offsetMin = _safeArea.offsetMax = Vector2.zero;
    }
}
