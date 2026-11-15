using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>Small landscape settings surface for the persistent shared AR-content selection.</summary>
public sealed class SettingsSceneController : MonoBehaviour
{
    private static readonly Color32 Surface = new(11, 30, 45, 255);
    private static readonly Color32 SelectedBorder = new(92, 216, 211, 255);
    private static readonly Color32 IdleBorder = new(39, 67, 83, 255);
    private readonly ContentCard[] _cards = new ContentCard[2];
    private RectTransform _safeArea;
    private Rect _lastSafeArea;
    private Vector2Int _lastScreenSize;

    private readonly struct ContentCard
    {
        public readonly ARContentType Type;
        public readonly Image Border;
        public readonly Text SelectionMark;

        public ContentCard(ARContentType type, Image border, Text selectionMark)
        {
            Type = type;
            Border = border;
            SelectionMark = selectionMark;
        }
    }

    private void Awake()
    {
        HomeScreenController.ConfigureEventSystem();
        BuildUi(ARContentSelection.Selected);
    }

    private void Update()
    {
        ApplySafeArea(Screen.safeArea, new Vector2Int(Screen.width, Screen.height));
#if ENABLE_INPUT_SYSTEM
        var backPressed = Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame;
#else
        var backPressed = Input.GetKeyDown(KeyCode.Escape);
#endif
        if (backPressed)
            SceneManager.LoadScene(HomeScreenController.HomeSceneName);
    }

    private void BuildUi(ARContentType selection)
    {
        if (_safeArea != null)
            return;
        var canvasObject = new GameObject("Settings Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasObject.transform.SetParent(transform, false);
        var canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.overrideSorting = true;
        canvas.sortingOrder = 2500;
        var scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1280f, 720f);
        scaler.matchWidthOrHeight = 1f;

        var backdrop = HomeScreenController.CreateImage(canvasObject.transform, "Backdrop", new Color(0.005f, 0.025f, 0.05f, 1f));
        HomeScreenController.Stretch(backdrop.rectTransform);
        backdrop.raycastTarget = false;
        _safeArea = new GameObject("Safe Area", typeof(RectTransform)).GetComponent<RectTransform>();
        _safeArea.SetParent(canvasObject.transform, false);
        HomeScreenController.Stretch(_safeArea);
        ApplySafeArea(Screen.safeArea, new Vector2Int(Screen.width, Screen.height));

        CreateLabel(_safeArea, "Title", "AR objektum", new Vector2(0.5f, 0.79f), new Vector2(680f, 66f), 40, Color.white);
        var row = new GameObject("Model cards", typeof(RectTransform)).GetComponent<RectTransform>();
        row.SetParent(_safeArea, false);
        row.anchorMin = row.anchorMax = new Vector2(.5f, .51f);
        row.sizeDelta = new Vector2(574f, 192f);
        _cards[0] = CreateContentCard(row, ARContentType.Cube, "Kocka", "Cube", -147f);
        _cards[1] = CreateContentCard(row, ARContentType.RoofCanopy, "El\u0151tet\u0151", "RoofCanopy", 147f);

        var back = CreateButton(_safeArea, "BackHomeButton", "HOME", new Vector2(0.5f, 0.2f), new Vector2(190f, 58f));
        back.onClick.AddListener(() => SceneManager.LoadScene(HomeScreenController.HomeSceneName));
        RefreshSelection(selection);
    }

    private ContentCard CreateContentCard(Transform parent, ARContentType type, string name, string previewName, float x)
    {
        var root = new GameObject($"{type}Button", typeof(RectTransform), typeof(Image), typeof(Button));
        root.transform.SetParent(parent, false);
        var rect = root.GetComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f);
        rect.anchoredPosition = new Vector2(x, 0f);
        rect.sizeDelta = new Vector2(280f, 192f);
        var border = root.GetComponent<Image>();
        var button = root.GetComponent<Button>();
        button.targetGraphic = border;
        var body = HomeScreenController.CreateImage(root.transform, "Card surface", Surface);
        HomeScreenController.Stretch(body.rectTransform);
        body.rectTransform.offsetMin = new Vector2(3f, 3f);
        body.rectTransform.offsetMax = new Vector2(-3f, -3f);
        body.raycastTarget = false;

        var previewObject = new GameObject("Preview", typeof(RectTransform), typeof(RawImage));
        previewObject.transform.SetParent(root.transform, false);
        var preview = previewObject.GetComponent<RawImage>();
        preview.rectTransform.anchorMin = preview.rectTransform.anchorMax = new Vector2(.5f, 1f);
        preview.rectTransform.pivot = new Vector2(.5f, 1f);
        preview.rectTransform.anchoredPosition = new Vector2(0f, -12f);
        preview.rectTransform.sizeDelta = new Vector2(256f, 128f);
        preview.texture = Resources.Load<Texture2D>($"SettingsModelPreviews/{previewName}");
        preview.raycastTarget = false;

        var title = CreateLabel(root.transform, "Model name", name, new Vector2(.5f, 0f), new Vector2(252f, 43f), 26, Color.white);
        title.rectTransform.anchoredPosition = new Vector2(0f, 27f);
        title.alignment = TextAnchor.MiddleLeft;
        var selected = CreateLabel(root.transform, "Selected badge", "KIV\u00c1LASZTVA", new Vector2(.5f, 0f),
            new Vector2(252f, 43f), 14, SelectedBorder);
        selected.rectTransform.anchoredPosition = new Vector2(0f, 27f);
        selected.alignment = TextAnchor.MiddleRight;
        button.onClick.AddListener(() =>
        {
            ARContentSelection.Set(type);
            RefreshSelection(ARContentSelection.Selected);
        });
        return new ContentCard(type, border, selected);
    }

    private void RefreshSelection(ARContentType selection)
    {
        foreach (var card in _cards)
        {
            var selected = card.Type == selection;
            card.Border.color = selected ? SelectedBorder : IdleBorder;
            card.SelectionMark.gameObject.SetActive(selected);
        }
    }

    private void ApplySafeArea(Rect area, Vector2Int size)
    {
        if (_safeArea == null || size.x <= 0 || size.y <= 0 || (_lastSafeArea == area && _lastScreenSize == size))
            return;
        _lastSafeArea = area;
        _lastScreenSize = size;
        _safeArea.anchorMin = new Vector2(area.xMin / size.x, area.yMin / size.y);
        _safeArea.anchorMax = new Vector2(area.xMax / size.x, area.yMax / size.y);
        _safeArea.offsetMin = _safeArea.offsetMax = Vector2.zero;
    }

    private static Button CreateButton(Transform parent, string name, string text, Vector2 anchor, Vector2 size)
    {
        var buttonObject = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
        buttonObject.transform.SetParent(parent, false);
        var rect = buttonObject.GetComponent<RectTransform>();
        rect.anchorMin = anchor;
        rect.anchorMax = anchor;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = size;
        var image = buttonObject.GetComponent<Image>();
        image.color = new Color(0.02f, 0.11f, 0.18f, 0.96f);
        var button = buttonObject.GetComponent<Button>();
        button.targetGraphic = image;
        CreateLabel(buttonObject.transform, "Label", text, new Vector2(0.5f, 0.5f), size - new Vector2(20f, 20f), 25, Color.white);
        return button;
    }

    private static Text CreateLabel(Transform parent, string name, string text, Vector2 anchor, Vector2 size, int fontSize, Color color)
    {
        var labelObject = new GameObject(name, typeof(RectTransform), typeof(Text));
        labelObject.transform.SetParent(parent, false);
        var rect = labelObject.GetComponent<RectTransform>();
        rect.anchorMin = anchor;
        rect.anchorMax = anchor;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = size;
        var label = labelObject.GetComponent<Text>();
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.text = text;
        label.fontSize = fontSize;
        label.fontStyle = FontStyle.Bold;
        label.color = color;
        label.alignment = TextAnchor.MiddleCenter;
        label.raycastTarget = false;
        return label;
    }
}
