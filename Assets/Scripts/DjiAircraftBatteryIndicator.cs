using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Small DroneView-only aircraft-battery HUD. It consumes the shared pose
/// snapshot and never creates its own DJI listener or polling path.
/// </summary>
[DisallowMultipleComponent]
internal sealed class DjiAircraftBatteryIndicator : MonoBehaviour
{
    private const float RefreshIntervalSeconds = 0.2f;
    private const float TopMargin = 18f;
    private const float RightMargin = 18f;

    private DjiCameraPoseProvider _telemetryProvider;
    private Text _label;
    private RectTransform _safeArea;
    private Rect _lastSafeArea;
    private Vector2Int _lastScreenSize;
    private string _lastText;
    private float _nextRefreshTime;

    private void Awake()
    {
        BuildUi();
    }

    private void Update()
    {
        UpdateSafeArea();
        if (Time.unscaledTime < _nextRefreshTime)
            return;

        _nextRefreshTime = Time.unscaledTime + RefreshIntervalSeconds;
        _telemetryProvider ??= FindFirstObjectByType<DjiCameraPoseProvider>();
        var batteryPercent = _telemetryProvider?.LatestAircraftBatteryPercent;
        var text = batteryPercent.HasValue ? $"BAT {batteryPercent.Value}%" : "BAT --";
        if (text == _lastText)
            return;

        _lastText = text;
        _label.text = text;
        Debug.Log(
            $"DJI_BATTERY_HUD displayed={text} sourceValue={(batteryPercent.HasValue ? batteryPercent.Value.ToString() : "null")} " +
            $"valid={batteryPercent.HasValue}");
    }

    private void BuildUi()
    {
        var canvasObject = new GameObject(
            "DJI Aircraft Battery Indicator Canvas",
            typeof(RectTransform),
            typeof(Canvas),
            typeof(GraphicRaycaster));
        canvasObject.transform.SetParent(transform, false);

        var canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.overrideSorting = true;
        canvas.sortingOrder = 3001;

        var safeAreaObject = new GameObject("Safe Area", typeof(RectTransform));
        safeAreaObject.transform.SetParent(canvasObject.transform, false);
        _safeArea = safeAreaObject.GetComponent<RectTransform>();
        UpdateSafeArea(force: true);

        var labelObject = new GameObject("Aircraft Battery", typeof(RectTransform), typeof(Text));
        labelObject.transform.SetParent(_safeArea, false);
        var labelRect = labelObject.GetComponent<RectTransform>();
        labelRect.anchorMin = new Vector2(1f, 1f);
        labelRect.anchorMax = new Vector2(1f, 1f);
        labelRect.pivot = new Vector2(1f, 1f);
        labelRect.anchoredPosition = new Vector2(-RightMargin, -TopMargin);
        labelRect.sizeDelta = new Vector2(120f, 42f);

        _label = labelObject.GetComponent<Text>();
        _label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        _label.fontSize = 21;
        _label.fontStyle = FontStyle.Bold;
        _label.alignment = TextAnchor.MiddleRight;
        _label.color = new Color32(99, 230, 107, 255);
        _label.raycastTarget = false;
        _label.text = "BAT --";
    }

    private void UpdateSafeArea(bool force = false)
    {
        var safeArea = Screen.safeArea;
        var screenSize = new Vector2Int(Screen.width, Screen.height);
        if (!force && safeArea == _lastSafeArea && screenSize == _lastScreenSize)
            return;

        _lastSafeArea = safeArea;
        _lastScreenSize = screenSize;
        _safeArea.anchorMin = new Vector2(safeArea.xMin / screenSize.x, safeArea.yMin / screenSize.y);
        _safeArea.anchorMax = new Vector2(safeArea.xMax / screenSize.x, safeArea.yMax / screenSize.y);
        _safeArea.offsetMin = Vector2.zero;
        _safeArea.offsetMax = Vector2.zero;
    }
}
