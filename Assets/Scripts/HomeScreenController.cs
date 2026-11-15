using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
#endif

/// <summary>
/// Builds the Boot screen with edge-to-edge artwork and safe-area controls.
/// </summary>
public sealed class HomeScreenController : MonoBehaviour
{
    internal const string HomeSceneName = "Boot";
    internal const string DroneSceneName = "DroneView";
    internal const string SettingsSceneName = "SettingsScene";

    private const string ArtworkResourcePath = "HomeArtwork/home";
    private const float ConnectionPollIntervalSeconds = 0.5f;
    private const string DisconnectedLabel = "Nincs dr\u00f3n csatlakoztatva";
    private const string ConnectedLabel = "AR-n\u00e9zet ind\u00edt\u00e1sa";

    private enum DjiConnectionState
    {
        Disconnected,
        UsbPermissionRequired,
        Connecting,
        Ready
    }

    private Button _droneModeButton;
    private Image _droneModeSurface;
    private Sprite _connectedSurface;
    private Sprite _disconnectedSurface;
    private Text _droneModeLabel;
    private RawImage _background;
    private RectTransform _safeArea;
    private Rect _lastSafeArea;
    private Vector2Int _lastScreenSize;
    private DjiConnectionState? _lastConnectionState;
    private float _nextConnectionPollTime;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void RegisterSceneCallback()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (scene.name == HomeSceneName)
        {
            if (FindFirstObjectByType<HomeScreenController>() == null)
                new GameObject("HomeScreen").AddComponent<HomeScreenController>();

            return;
        }

        if (scene.name == SettingsSceneName)
        {
            if (FindFirstObjectByType<SettingsSceneController>() == null)
                new GameObject("SettingsScreen").AddComponent<SettingsSceneController>();
            return;
        }

        if (scene.name == DroneSceneName &&
            FindFirstObjectByType<ModeHomeNavigationController>() == null)
        {
            new GameObject("Mode Home Navigation").AddComponent<ModeHomeNavigationController>();
        }
    }

    private void Awake()
    {
        ConfigureEventSystem();
        BuildUi();
        RefreshDjiConnectionState(force: true);
        Debug.Log("HOME_SCREEN_READY actions=DRONE_AR artwork=NAVY");
    }

    private void Update()
    {
        ApplySafeArea(Screen.safeArea, new Vector2Int(Screen.width, Screen.height));
        RefreshDjiConnectionState(force: false);
    }

    internal static void ConfigureEventSystem()
    {
        var eventSystem = EventSystem.current;
        if (eventSystem == null)
        {
            var eventSystemObject = new GameObject("HomeScreen EventSystem", typeof(EventSystem));
            eventSystem = eventSystemObject.GetComponent<EventSystem>();
        }

#if ENABLE_INPUT_SYSTEM
        var inputModule = eventSystem.GetComponent<InputSystemUIInputModule>();
        if (inputModule == null)
            inputModule = eventSystem.gameObject.AddComponent<InputSystemUIInputModule>();
        inputModule.AssignDefaultActions();
#endif
    }

    private void BuildUi()
    {
        var canvasObject = new GameObject(
            "HomeScreen Canvas",
            typeof(RectTransform),
            typeof(Canvas),
            typeof(CanvasScaler),
            typeof(GraphicRaycaster));
        canvasObject.transform.SetParent(transform, false);

        var canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.overrideSorting = true;
        canvas.sortingOrder = 2000;

        var scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1280, 720);
        scaler.matchWidthOrHeight = 1f;

        var artworkTexture = Resources.Load<Texture2D>(ArtworkResourcePath);
        if (artworkTexture != null)
        {
            var artwork = new GameObject("Home Artwork", typeof(RectTransform), typeof(RawImage));
            artwork.transform.SetParent(canvasObject.transform, false);
            _background = artwork.GetComponent<RawImage>();
            _background.texture = artworkTexture;
            _background.raycastTarget = false;
            Stretch(_background.rectTransform);
        }
        else
        {
            Debug.LogError($"HOME_ARTWORK_MISSING resource={ArtworkResourcePath}");
            var fallback = CreateImage(canvasObject.transform, "Home Background Fallback", new Color32(5, 16, 28, 255));
            Stretch(fallback.rectTransform);
            fallback.raycastTarget = false;
        }

        _safeArea = new GameObject("Safe Area", typeof(RectTransform)).GetComponent<RectTransform>();
        _safeArea.SetParent(canvasObject.transform, false);
        Stretch(_safeArea);
        ApplySafeArea(Screen.safeArea, new Vector2Int(Screen.width, Screen.height));

        _connectedSurface = Resources.Load<Sprite>("HomeArtwork/action-connected");
        _disconnectedSurface = Resources.Load<Sprite>("HomeArtwork/action-disconnected");
        _droneModeButton = CreateButton(_safeArea, "DroneModeButton", LoadDroneAr, _disconnectedSurface, out _droneModeSurface);
        var actionRect = _droneModeSurface.rectTransform;
        actionRect.anchorMin = new Vector2(.31f, .34f);
        actionRect.anchorMax = new Vector2(.69f, .34f);
        actionRect.anchoredPosition = Vector2.zero;
        actionRect.sizeDelta = new Vector2(0f, 66f);
        _droneModeLabel = CreateLabel(actionRect);
        ApplyDroneButtonStyle(false);

        CreateButton(_safeArea, "GearSettingsButton", LoadSettings, _disconnectedSurface, out var settingsSurface);
        var settingsRect = settingsSurface.rectTransform;
        settingsRect.anchorMin = settingsRect.anchorMax = settingsRect.pivot = Vector2.one;
        settingsRect.anchoredPosition = new Vector2(-24f, -24f);
        settingsRect.sizeDelta = new Vector2(60f, 60f);
        var icon = CreateImage(settingsRect, "Settings Icon", new Color32(218, 240, 250, 255));
        icon.sprite = Resources.Load<Sprite>("HomeArtwork/settings-gear");
        Stretch(icon.rectTransform, 15f);
        icon.raycastTarget = false;
    }

    private void LoadSettings()
    {
        Debug.Log("HOME_NAVIGATION destination=SETTINGS");
        SceneManager.LoadScene(SettingsSceneName);
    }

    private void LoadDroneAr()
    {
        RefreshDjiConnectionState(force: true);
        if (_lastConnectionState != DjiConnectionState.Ready)
        {
            Debug.Log($"HOME_NAVIGATION_BLOCKED destination=DRONE_AR state={ToLogState(_lastConnectionState ?? DjiConnectionState.Disconnected)}");
            return;
        }

        Debug.Log("HOME_NAVIGATION destination=DRONE_AR mode=STANDALONE_BOARD_FRAME");
        SceneManager.LoadScene(DroneSceneName);
    }

    private void RefreshDjiConnectionState(bool force)
    {
        if (!force && Time.unscaledTime < _nextConnectionPollTime)
            return;

        _nextConnectionPollTime = Time.unscaledTime + ConnectionPollIntervalSeconds;
        ApplyDjiConnectionStatus(DjiHomeConnectionProvider.GetStatus());
    }

    private void ApplyDjiConnectionStatus(DjiHomeConnectionProvider.Status status)
    {
        var connectionState = ResolveConnectionState(status);
        if (_lastConnectionState == connectionState)
            return;

        _lastConnectionState = connectionState;
        var ready = connectionState == DjiConnectionState.Ready;
        ApplyDroneButtonStyle(ready);

        Debug.Log($"HOME_DJI_CONNECTION_STATE={ToLogState(connectionState)}");
        Debug.Log($"HOME_DRONE_BUTTON interactable={ready}");
    }

    private static DjiConnectionState ResolveConnectionState(DjiHomeConnectionProvider.Status status)
    {
        if (!status.expectedUsbDeviceAttached)
            return DjiConnectionState.Disconnected;
        if (!status.usbPermissionGranted)
            return DjiConnectionState.UsbPermissionRequired;
        return status.productConnected ? DjiConnectionState.Ready : DjiConnectionState.Connecting;
    }

    private void ApplyDroneButtonStyle(bool ready)
    {
        _droneModeButton.interactable = ready;
        _droneModeSurface.sprite = ready ? _connectedSurface : _disconnectedSurface;
        _droneModeLabel.text = ready ? ConnectedLabel : DisconnectedLabel;
        _droneModeLabel.color = ready ? new Color32(255, 255, 255, 255) : new Color32(199, 212, 222, 255);
    }

    private static string ToLogState(DjiConnectionState state) => state switch
    {
        DjiConnectionState.Disconnected => "DISCONNECTED",
        DjiConnectionState.UsbPermissionRequired => "USB_PERMISSION_REQUIRED",
        DjiConnectionState.Connecting => "CONNECTING",
        DjiConnectionState.Ready => "READY",
        _ => "DISCONNECTED"
    };

    private static Button CreateButton(Transform parent, string name, UnityEngine.Events.UnityAction onClick,
        Sprite sprite, out Image surface)
    {
        surface = CreateImage(parent, name, Color.white);
        surface.sprite = sprite;
        surface.type = Image.Type.Sliced;
        var button = surface.gameObject.AddComponent<Button>();
        button.targetGraphic = surface;
        button.transition = Selectable.Transition.None;
        button.onClick.AddListener(onClick);
        return button;
    }

    private static Text CreateLabel(Transform parent)
    {
        var labelObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
        labelObject.transform.SetParent(parent, false);
        var text = labelObject.GetComponent<Text>();
        Stretch(text.rectTransform);
        text.rectTransform.offsetMin = new Vector2(24f, 8f);
        text.rectTransform.offsetMax = new Vector2(-24f, -8f);
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = 28;
        text.fontStyle = FontStyle.Bold;
        text.alignment = TextAnchor.MiddleCenter;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.raycastTarget = false;
        return text;
    }

    private void ApplySafeArea(Rect area, Vector2Int screenSize)
    {
        if (screenSize.x <= 0 || screenSize.y <= 0 || area.width <= 0 || area.height <= 0 ||
            (_lastSafeArea == area && _lastScreenSize == screenSize)) return;
        _lastSafeArea = area;
        _lastScreenSize = screenSize;

        if (_background != null)
        {
            var texture = _background.texture;
            var relativeAspect = (float)screenSize.x * texture.height / (screenSize.y * texture.width);
            var cropWidth = Mathf.Min(relativeAspect, 1f);
            var cropHeight = Mathf.Min(1f / relativeAspect, 1f);
            _background.uvRect = new Rect((1f - cropWidth) * .5f, (1f - cropHeight) * .5f, cropWidth, cropHeight);
        }

        // Mirror the larger side inset so the action stays centered under the artwork logo.
        var inset = Mathf.Clamp01(Mathf.Max(area.xMin, screenSize.x - area.xMax) / screenSize.x);
        _safeArea.anchorMin = new Vector2(inset, area.yMin / screenSize.y);
        _safeArea.anchorMax = new Vector2(1f - inset, area.yMax / screenSize.y);
        _safeArea.offsetMin = _safeArea.offsetMax = Vector2.zero;
    }

    internal static Image CreateImage(Transform parent, string name, Color color)
    {
        var imageObject = new GameObject(name, typeof(RectTransform), typeof(Image));
        imageObject.transform.SetParent(parent, false);
        var image = imageObject.GetComponent<Image>();
        image.color = color;
        return image;
    }

    internal static void Stretch(RectTransform rectTransform, float inset = 0f)
    {
        rectTransform.anchorMin = Vector2.zero;
        rectTransform.anchorMax = Vector2.one;
        rectTransform.offsetMin = new Vector2(inset, inset);
        rectTransform.offsetMax = new Vector2(-inset, -inset);
    }
}

/// <summary>Provides a consistent Home action for both independent camera modes.</summary>
internal sealed class ModeHomeNavigationController : MonoBehaviour
{
    private bool _returningHome;

    private void Awake()
    {
        HomeScreenController.ConfigureEventSystem();
        BuildUi();
    }

    private void Update()
    {
#if ENABLE_INPUT_SYSTEM
        var backPressed = Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame;
#else
        var backPressed = Input.GetKeyDown(KeyCode.Escape);
#endif
        if (backPressed)
            ReturnHome();
    }

    private void BuildUi()
    {
        var canvasObject = new GameObject(
            "Mode Home Navigation Canvas",
            typeof(RectTransform),
            typeof(Canvas),
            typeof(GraphicRaycaster));
        canvasObject.transform.SetParent(transform, false);
        var canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.overrideSorting = true;
        canvas.sortingOrder = 3000;

        var buttonObject = new GameObject("HomeButton", typeof(RectTransform), typeof(Image), typeof(Button));
        buttonObject.transform.SetParent(canvasObject.transform, false);
        var rect = buttonObject.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(24f, -24f);
        rect.sizeDelta = new Vector2(154f, 58f);

        var image = buttonObject.GetComponent<Image>();
        image.color = new Color(0.015f, 0.07f, 0.1f, 0.9f);
        var button = buttonObject.GetComponent<Button>();
        button.targetGraphic = image;
        button.onClick.AddListener(ReturnHome);

        var labelObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
        labelObject.transform.SetParent(buttonObject.transform, false);
        HomeScreenController.Stretch(labelObject.GetComponent<RectTransform>(), 8f);
        var label = labelObject.GetComponent<Text>();
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.text = "HOME";
        label.fontSize = 24;
        label.fontStyle = FontStyle.Bold;
        label.color = Color.white;
        label.alignment = TextAnchor.MiddleCenter;
        label.raycastTarget = false;
    }

    private void ReturnHome()
    {
        if (_returningHome)
            return;

        _returningHome = true;
        Debug.Log($"MODE_HOME_NAVIGATION source={SceneManager.GetActiveScene().name} destination={HomeScreenController.HomeSceneName}");
        SceneManager.LoadScene(HomeScreenController.HomeSceneName);
    }
}
