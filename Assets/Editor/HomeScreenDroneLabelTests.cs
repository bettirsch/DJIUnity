using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

public sealed class HomeScreenDroneLabelTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string ReadyLabel = "AR-n\u00e9zet ind\u00edt\u00e1sa";
    private const string DisconnectedLabel = "Nincs dr\u00f3n csatlakoztatva";
    private static readonly DjiHomeConnectionProvider.Status Ready = new()
    {
        expectedUsbDeviceAttached = true,
        usbPermissionGranted = true,
        sdkRegistered = true,
        productConnected = true
    };

    [Test]
    public void ReadyLabelAppearsOnConnectReconnectAndReturnHome()
    {
        var home = CreateHome();
        try
        {
            var button = GetButton(home);
            var label = button.GetComponentInChildren<Text>();
            Apply(home, default);
            Assert.That(button.interactable, Is.False);
            Assert.That(label.text, Is.EqualTo(DisconnectedLabel));
            LogAssert.Expect(LogType.Log, "HOME_NAVIGATION_BLOCKED destination=DRONE_AR state=DISCONNECTED");
            button.onClick.Invoke();
            Apply(home, new DjiHomeConnectionProvider.Status { expectedUsbDeviceAttached = true });
            Assert.That(label.text, Is.EqualTo(DisconnectedLabel));
            Apply(home, new DjiHomeConnectionProvider.Status { expectedUsbDeviceAttached = true, usbPermissionGranted = true });
            Assert.That(button.interactable, Is.False);
            Assert.That(label.text, Is.EqualTo(DisconnectedLabel));
            Apply(home, Ready);
            Assert.That(button.interactable, Is.True);
            Assert.That(label.text, Is.EqualTo(ReadyLabel));
            Apply(home, default);
            Assert.That(button.interactable, Is.False);
            Assert.That(label.text, Is.EqualTo(DisconnectedLabel));
            Apply(home, Ready);
            Assert.That(button.interactable, Is.True);
            Assert.That(label.text, Is.EqualTo(ReadyLabel));
        }
        finally { Object.DestroyImmediate(home.gameObject); }

        var reopened = CreateHome();
        try
        {
            Apply(reopened, Ready);
            var button = GetButton(reopened);
            Assert.That(button.interactable, Is.True);
            Assert.That(button.GetComponentInChildren<Text>().text, Is.EqualTo(ReadyLabel));
        }
        finally { Object.DestroyImmediate(reopened.gameObject); }
    }

    [TestCase(960, 540, 0, 0)]
    [TestCase(1280, 720, 0, 0)]
    [TestCase(1560, 720, 54, 18)]
    public void BothStatesFitTheSameRoundedButtonWithinTheLandscapeSafeArea(int width, int height, int leftInset, int rightInset)
    {
        var home = CreateHome();
        try
        {
            var size = new Vector2Int(width, height);
            var safePixels = new Rect(leftInset, 0, width - leftInset - rightInset, height);
            ApplySafeArea(home, safePixels, size);
            var canvas = home.GetComponentInChildren<Canvas>();
            canvas.GetComponent<CanvasScaler>().enabled = false;
            canvas.renderMode = RenderMode.WorldSpace;
            var canvasSize = new Vector2((float)width / height * 720, 720);
            canvas.GetComponent<RectTransform>().sizeDelta = canvasSize;
            Canvas.ForceUpdateCanvases();

            var background = home.GetComponentsInChildren<RawImage>().Single();
            Assert.That(background.texture.name, Is.EqualTo("home"));
            Assert.That(background.raycastTarget, Is.False);
            var imageRect = background.rectTransform.rect;
            Assert.That(imageRect.width, Is.EqualTo(canvasSize.x).Within(.1f));
            Assert.That(imageRect.height, Is.EqualTo(canvasSize.y).Within(.1f));
            var uv = background.uvRect;
            Assert.That(Vector2.Distance(uv.center, new Vector2(.5f, .5f)), Is.LessThan(.001f));
            Assert.That(Mathf.Max(uv.width, uv.height), Is.EqualTo(1f));
            Assert.That(imageRect.width / imageRect.height,
                Is.EqualTo(background.texture.width * uv.width / (background.texture.height * uv.height)).Within(.001f));

            var button = GetButton(home);
            var panel = button.GetComponent<Image>();
            var label = button.GetComponentInChildren<Text>();
            var safe = (RectTransform)button.transform.parent;
            var panelRect = panel.rectTransform.rect;
            Assert.That(panelRect.width, Is.EqualTo(safe.rect.width * .38f).Within(.1f));
            Assert.That(panelRect.height, Is.EqualTo(66f).Within(.1f));
            Assert.That(panel.rectTransform.localPosition.x, Is.Zero.Within(.01f));
            Assert.That(button.targetGraphic, Is.SameAs(panel));
            Assert.That(panel.raycastTarget, Is.True);
            Assert.That(label.raycastTarget, Is.False);
            Assert.That(button.transition, Is.EqualTo(Selectable.Transition.None));
            Assert.That(home.GetComponentsInChildren<CanvasGroup>(), Is.Empty);
            Assert.That(panel.type, Is.EqualTo(Image.Type.Sliced));

            Apply(home, default);
            Assert.That(button.interactable, Is.False);
            var disabledSurface = panel.sprite;
            Assert.That(disabledSurface.name, Is.EqualTo("action-disconnected"));
            AssertLabelFits(label, DisconnectedLabel, "\u00f3");
            Assert.That(label.color.r, Is.LessThan(1f));

            Apply(home, Ready);
            Assert.That(button.interactable, Is.True);
            Assert.That(panel.rectTransform.rect.size, Is.EqualTo(panelRect.size));
            Assert.That(panel.sprite.name, Is.EqualTo("action-connected"));
            Assert.That(panel.sprite.border, Is.EqualTo(disabledSurface.border));
            Assert.That(panel.sprite.rect.size, Is.EqualTo(disabledSurface.rect.size));
            Assert.That(label.color, Is.EqualTo(Color.white));
            AssertLabelFits(label, ReadyLabel, "\u00e9\u00ed\u00e1");

            var settings = safe.Find("GearSettingsButton").GetComponent<Button>();
            Assert.That(home.GetComponentsInChildren<Button>(), Has.Length.EqualTo(2));
            Assert.That(settings.targetGraphic.raycastTarget, Is.True);
            Assert.That(settings.transform.Find("Settings Icon").GetComponent<Image>().sprite.name, Is.EqualTo("settings-gear"));
            var settingsRect = (RectTransform)settings.transform;
            Assert.That(settingsRect.anchorMax, Is.EqualTo(Vector2.one));
            Assert.That(settingsRect.anchoredPosition, Is.EqualTo(new Vector2(-24, -24)));

            var output = Environment.GetEnvironmentVariable("DJI_HOME_PREVIEW_DIR");
            if (!string.IsNullOrEmpty(output) && height == 720)
                CaptureStates(home, canvas, size, output);
        }
        finally { Object.DestroyImmediate(home.gameObject); }
    }

    [Test]
    public void BootHasNoSecondSerializedHomeButtonOrLogoCanvas()
    {
        const string path = "Assets/Scenes/Boot.unity";
        var scene = SceneManager.GetSceneByPath(path);
        var wasLoaded = scene.isLoaded;
        if (!wasLoaded) scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
        try { Assert.That(scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Canvas>(true)), Is.Empty); }
        finally { if (!wasLoaded) EditorSceneManager.CloseScene(scene, true); }
    }

    private static HomeScreenController CreateHome()
    {
        var home = new GameObject("Test Home").AddComponent<HomeScreenController>();
        typeof(HomeScreenController).GetMethod("BuildUi", PrivateInstance).Invoke(home, null);
        return home;
    }

    private static void Apply(HomeScreenController home, DjiHomeConnectionProvider.Status status) =>
        typeof(HomeScreenController).GetMethod("ApplyDjiConnectionStatus", PrivateInstance)
            .Invoke(home, new object[] { status });

    private static Button GetButton(HomeScreenController home) => home.transform
        .Find("HomeScreen Canvas/Safe Area/DroneModeButton").GetComponent<Button>();

    private static void ApplySafeArea(HomeScreenController home, Rect area, Vector2Int size) =>
        typeof(HomeScreenController).GetMethod("ApplySafeArea", PrivateInstance)
            .Invoke(home, new object[] { area, size });

    private static void AssertLabelFits(Text label, string expected, string accented)
    {
        Assert.That(label.text, Is.EqualTo(expected));
        foreach (var c in accented) Assert.That(label.font.HasCharacter(c), Is.True);
        Assert.That(label.preferredWidth, Is.Positive.And.LessThan(label.rectTransform.rect.width));
        Assert.That(label.preferredHeight, Is.Positive.And.LessThan(label.rectTransform.rect.height));
        TestContext.WriteLine($"{expected}: text {label.preferredWidth}x{label.preferredHeight}, available {label.rectTransform.rect.size}");
    }

    private static void CaptureStates(HomeScreenController home, Canvas canvas, Vector2Int size, string directory)
    {
        const int previewLayer = 31;
        var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/Settings/Mobile_Renderer.asset");
        var video = renderer.rendererFeatures.OfType<DJIBackgroundURPFeature>().Single();
        var videoWasActive = video.isActive;
        var camera = new GameObject("Home preview camera", typeof(Camera), typeof(UniversalAdditionalCameraData)).GetComponent<Camera>();
        var target = RenderTexture.GetTemporary(size.x, size.y, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        var previous = RenderTexture.active;
        var image = new Texture2D(size.x, size.y, TextureFormat.RGB24, false);
        try
        {
            video.SetActive(false);
            camera.enabled = false;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.allowHDR = false;
            camera.cullingMask = 1 << previewLayer;
            camera.orthographic = true;
            camera.orthographicSize = .5f;
            camera.transform.position = new Vector3(0, 0, -2);
            camera.nearClipPlane = .01f;
            camera.farClipPlane = 10f;
            camera.GetUniversalAdditionalCameraData().renderPostProcessing = false;
            camera.targetTexture = target;
            foreach (var item in home.GetComponentsInChildren<Transform>(true)) item.gameObject.layer = previewLayer;
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = camera;
            canvas.planeDistance = 1f;

            Directory.CreateDirectory(directory);
            foreach (var ready in new[] { false, true })
            {
                Apply(home, ready ? Ready : default);
                Canvas.ForceUpdateCanvases();
                RenderPipeline.SubmitRenderRequest(camera, new RenderPipeline.StandardRequest { destination = target });
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, size.x, size.y), 0, 0);
                image.Apply();
                File.WriteAllBytes(Path.Combine(directory, $"home-{(ready ? "connected" : "disconnected")}-{size.x}x{size.y}.png"), image.EncodeToPNG());
                AssertControlsRendered(home, camera, image, ready);
            }
        }
        finally
        {
            camera.targetTexture = null;
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
            Object.DestroyImmediate(image);
            Object.DestroyImmediate(camera.gameObject);
            video.SetActive(videoWasActive);
        }
    }

    private static void AssertControlsRendered(HomeScreenController home, Camera camera, Texture2D image, bool ready)
    {
        var button = GetButton(home);
        var panel = (RectTransform)button.transform;
        var top = RectTransformUtility.WorldToScreenPoint(camera, panel.TransformPoint(new Vector3(0, panel.rect.yMax, 0)));
        var border = Enumerable.Range(-4, 6).Select(dy => image.GetPixel(Mathf.RoundToInt(top.x), Mathf.RoundToInt(top.y) + dy));
        Assert.That(border.Any(c => c.b > (ready ? .55f : .28f) && c.g > .18f && c.b > c.r + .1f), Is.True,
            $"The {image.width}x{image.height} {(ready ? "connected" : "disabled")} outline must be visible.");

        var icon = (RectTransform)button.transform.parent.Find("GearSettingsButton/Settings Icon");
        var center = RectTransformUtility.WorldToScreenPoint(camera, icon.TransformPoint(icon.rect.center));
        var gear = image.GetPixels(Mathf.RoundToInt(center.x) - 12, Mathf.RoundToInt(center.y) - 12, 24, 24);
        Assert.That(gear.Count(c => c.g > .65f && c.b > .65f), Is.GreaterThan(40), "The settings gear must be visible.");
    }
}
