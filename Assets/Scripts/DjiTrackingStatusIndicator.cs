using UnityEngine;
using UnityEngine.UI;

public enum DjiTrackingStatus
{
    Waiting,
    Visual,
    Telemetry,
    TelemetryLimited,
    Lost
}

/// <summary>Observes existing render authority; never resolves or updates a tracking pose.</summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(1000)]
public sealed class DjiTrackingStatusIndicator : MonoBehaviour
{
    private DjiSharedReferenceCubeOverlay _overlay;
    private DjiWorldLocalizationShadow _world;
    private Canvas _canvas;
    private RectTransform _safeArea;
    private RectTransform _panel;
    private Text _label;
    private Rect _lastSafeArea;
    private Vector2Int _lastSize;
    private bool _hasDisplayedStatus;

    public DjiTrackingStatus DisplayedStatus { get; private set; }

    public void Configure(DjiSharedReferenceCubeOverlay overlay, DjiWorldLocalizationShadow world)
    {
        _overlay = overlay;
        _world = world;
        EnsureUi();
        RefreshStatus();
    }

    public static DjiTrackingStatus Resolve(DjiRenderPoseSource source, bool hasAuthoritativeReference, bool translationLimited)
    {
        if (!hasAuthoritativeReference)
            return DjiTrackingStatus.Waiting;

        return source switch
        {
            DjiRenderPoseSource.Visual => DjiTrackingStatus.Visual,
            DjiRenderPoseSource.TelemetryPropagated =>
                translationLimited ? DjiTrackingStatus.TelemetryLimited : DjiTrackingStatus.Telemetry,
            _ => DjiTrackingStatus.Lost
        };
    }

    public static string LabelFor(DjiTrackingStatus status) => status switch
    {
        DjiTrackingStatus.Visual => "VISUAL",
        DjiTrackingStatus.Telemetry => "TELEMETRY",
        DjiTrackingStatus.TelemetryLimited => "TELEMETRY (LIMITED)",
        DjiTrackingStatus.Lost => "TRACKING LOST",
        _ => "WAITING"
    };

    private void Awake() => EnsureUi();
    private void OnEnable() { if (_canvas != null) _canvas.enabled = true; }
    private void OnDisable() { if (_canvas != null) _canvas.enabled = false; }

    private void LateUpdate()
    {
        UpdateSafeArea();
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var source = _overlay != null ? _overlay.TrackingRenderPoseSource : DjiRenderPoseSource.None;
        bool hasReference = _overlay != null && _overlay.HasAuthoritativeVisualReference;
        var horizontal = _world != null ? _world.HorizontalGnssSmoothingShadow.estimate : default;
        // Qualify held/rejected/stale position sources without introducing a new quality rule.
        bool limited = _world == null || !_world.isActiveAndEnabled ||
                       !_world.ProductionHorizontalGnssApplied ||
                       _world.ProductionHorizontalGnssState != HorizontalGnssSmoothingState.FollowingFreshGnss ||
                       !_world.ProductionVerticalAltitude.sourceFresh ||
                       horizontal.rawGnssTargetTimestampNs > horizontal.acceptedGnssTargetTimestampNs ||
                       (!string.IsNullOrEmpty(_world.ProductionHorizontalGnssReason) &&
                        _world.ProductionHorizontalGnssReason != "NO_NEW_GNSS_TARGET");
        var status = Resolve(source, hasReference, limited);
        if (_hasDisplayedStatus && DisplayedStatus == status)
            return;

        _hasDisplayedStatus = true;
        DisplayedStatus = status;
        _label.text = LabelFor(status);
        _label.color = status switch
        {
            DjiTrackingStatus.Visual => new Color32(99, 230, 107, 255),
            DjiTrackingStatus.Telemetry or DjiTrackingStatus.TelemetryLimited => new Color32(255, 200, 87, 255),
            DjiTrackingStatus.Lost => new Color32(255, 115, 107, 255),
            _ => new Color32(211, 218, 224, 255)
        };
    }

    private void EnsureUi()
    {
        if (_canvas != null)
            return;

        var root = new GameObject("DJI Tracking Status Canvas", typeof(RectTransform), typeof(Canvas));
        root.transform.SetParent(transform, false);
        _canvas = root.GetComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.overrideSorting = true;
        _canvas.sortingOrder = 3001;

        var safe = new GameObject("Safe Area", typeof(RectTransform));
        safe.transform.SetParent(root.transform, false);
        _safeArea = safe.GetComponent<RectTransform>();

        var panel = new GameObject("Tracking Status", typeof(RectTransform), typeof(Image));
        panel.transform.SetParent(_safeArea, false);
        _panel = panel.GetComponent<RectTransform>();
        _panel.anchorMin = _panel.anchorMax = _panel.pivot = new Vector2(0f, 1f);
        _panel.anchoredPosition = new Vector2(24f, -92f);
        _panel.sizeDelta = new Vector2(252f, 36f);
        var backing = panel.GetComponent<Image>();
        backing.color = new Color(0.015f, 0.07f, 0.1f, 0.88f);
        backing.raycastTarget = false;

        var label = new GameObject("Label", typeof(RectTransform), typeof(Text));
        label.transform.SetParent(panel.transform, false);
        var rect = label.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(12f, 0f);
        rect.offsetMax = new Vector2(-12f, 0f);
        _label = label.GetComponent<Text>();
        _label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        _label.fontSize = 18;
        _label.fontStyle = FontStyle.Bold;
        _label.alignment = TextAnchor.MiddleLeft;
        _label.raycastTarget = false;
        _label.horizontalOverflow = HorizontalWrapMode.Overflow;
        _label.verticalOverflow = VerticalWrapMode.Truncate;
        UpdateSafeArea();
        RefreshStatus();
    }

    private void UpdateSafeArea() => ApplySafeArea(Screen.safeArea, new Vector2Int(Screen.width, Screen.height));

    private void ApplySafeArea(Rect area, Vector2Int size)
    {
        if (size.x <= 0 || size.y <= 0 || _safeArea == null || (area == _lastSafeArea && size == _lastSize))
            return;
        _lastSafeArea = area;
        _lastSize = size;
        _safeArea.anchorMin = new Vector2(area.xMin / size.x, area.yMin / size.y);
        _safeArea.anchorMax = new Vector2(area.xMax / size.x, area.yMax / size.y);
        _safeArea.offsetMin = _safeArea.offsetMax = Vector2.zero;
        _panel.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, Mathf.Min(252f, Mathf.Max(0f, area.width - 48f)));
    }
}
