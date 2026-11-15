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
using Object = UnityEngine.Object;

public sealed class RoofCanopyLightingTests
{
    private Scene _previousScene;
    private Scene _lightingScene;
    private GameObject _content;

    [SetUp]
    public void SetUp()
    {
        _previousScene = SceneManager.GetActiveScene();
        _lightingScene = EditorSceneManager.OpenScene("Assets/Scenes/DroneView.unity", OpenSceneMode.Additive);
        SceneManager.SetActiveScene(_lightingScene);
    }

    [TearDown]
    public void TearDown()
    {
        if (_content != null) Object.DestroyImmediate(_content);
        if (_previousScene.IsValid() && _previousScene.isLoaded) SceneManager.SetActiveScene(_previousScene);
        if (_lightingScene.IsValid() && _lightingScene.isLoaded) EditorSceneManager.CloseScene(_lightingScene, true);
    }

    [Test]
    public void DroneView_HasOneShadowedKeyAndOneNonShadowingFill()
    {
        var lights = Object.FindObjectsByType<Light>(FindObjectsSortMode.None)
            .Where(light => light.gameObject.scene == _lightingScene).ToArray();
        Assert.That(lights.Length, Is.EqualTo(2));
        var light = RenderSettings.sun;
        Assert.That(lights, Does.Contain(light));
        Assert.That(light.enabled && light.gameObject.activeInHierarchy, Is.True);
        Assert.That(light.type, Is.EqualTo(LightType.Directional));
        Assert.That(RenderSettings.sun, Is.SameAs(light));
        Assert.That(light.shadows, Is.EqualTo(LightShadows.Hard));
        Assert.That(light.shadowStrength, Is.EqualTo(0.55f).Within(0.001f));
        Assert.That(light.intensity, Is.EqualTo(0.85f).Within(0.001f));
        Assert.That(light.cullingMask, Is.EqualTo(1 << 2));
        Assert.That(light.transform.forward.z, Is.LessThan(-0.5f));
        Assert.That(Mathf.Abs(light.transform.forward.x), Is.GreaterThan(0.2f));
        Assert.That(Object.FindObjectsByType<ReflectionProbe>(FindObjectsSortMode.None), Is.Empty);
        Assert.That(light.GetComponent<UniversalAdditionalLightData>().usePipelineSettings, Is.False);
        var fill = lights.Single(l => l != light);
        Assert.That(fill.type, Is.EqualTo(LightType.Directional));
        Assert.That(fill.enabled && fill.gameObject.activeInHierarchy, Is.True);
        Assert.That(fill.intensity, Is.EqualTo(.25f));
        Assert.That(fill.shadows, Is.EqualTo(LightShadows.None));
        Assert.That(fill.color, Is.EqualTo(new Color(.86f, .91f, 1f)));
        Assert.That(fill.cullingMask, Is.EqualTo(1 << 2));
        Assert.That(Vector3.Angle(fill.transform.forward, new Vector3(-.65f, .5f, -.25f)), Is.LessThan(.01f));
    }

    [Test]
    public void DroneView_FillLightsPostSidesFromAbove_NotDownwardEndcaps()
    {
        _content = RoofCanopyLightingPreview.CreateContent();
        var model = _content.GetComponentsInChildren<Transform>().Single(t => t.name == "BoardRoofCanopy");
        var canopyUp = model.TransformDirection(Vector3.up).normalized;
        var fill = Object.FindObjectsByType<Light>(FindObjectsSortMode.None)
            .Single(l => l.gameObject.scene == _lightingScene && l != RenderSettings.sun);
        var incoming = -fill.transform.forward;

        Assert.That(Vector3.Angle(canopyUp, Vector3.forward), Is.LessThan(.001f));
        Assert.That(Vector3.Dot(incoming, canopyUp), Is.GreaterThan(0f));
        Assert.That(Mathf.Max(0f, Vector3.Dot(incoming, -canopyUp)), Is.EqualTo(0f));
        Assert.That(Vector3.Dot(incoming, Vector3.right), Is.GreaterThan(.7f));
        Assert.That(Vector3.Dot(incoming, Vector3.down), Is.GreaterThan(.5f));
    }

    [Test]
    public void DroneView_HasModestNonDirectionalAmbientFill()
    {
        Assert.That(RenderSettings.ambientMode, Is.EqualTo(AmbientMode.Flat));
        Assert.That(RenderSettings.ambientLight.r, Is.EqualTo(0.40f).Within(0.001f));
        Assert.That(RenderSettings.ambientLight.g, Is.EqualTo(0.42f).Within(0.001f));
        Assert.That(RenderSettings.ambientLight.b, Is.EqualTo(0.46f).Within(0.001f));
        Assert.That(RenderSettings.defaultReflectionMode, Is.EqualTo(DefaultReflectionMode.Custom));
        Assert.That(RenderSettings.reflectionIntensity, Is.EqualTo(1f));
        Assert.That(RenderSettings.customReflectionTexture, Is.SameAs(AssetDatabase.LoadAssetAtPath<Cubemap>(RoofCanopyLightingSetup.EnvironmentPath)));
    }

    [Test]
    public void MobilePipelineAndCanopyMaterials_PreserveStructureShadowsAndRoofTransmission()
    {
        var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>("Assets/Settings/Mobile_RPAsset.asset");
        Assert.That(pipeline.supportsMainLightShadows, Is.True);
        Assert.That(pipeline.mainLightRenderingMode, Is.EqualTo(LightRenderingMode.PerPixel));
        Assert.That(pipeline.shadowDistance, Is.GreaterThan(10f));
        _content = RoofCanopyLightingPreview.CreateContent();
        var renderers = _content.GetComponentsInChildren<MeshRenderer>();
        Assert.That(renderers.Length, Is.EqualTo(3));
        foreach (var renderer in renderers)
        {
            Assert.That(renderer.gameObject.layer, Is.EqualTo(2));
            var isRoof = renderer.name == "roof";
            Assert.That(renderer.shadowCastingMode, Is.EqualTo(isRoof ? ShadowCastingMode.Off : ShadowCastingMode.On));
            Assert.That(renderer.receiveShadows, Is.True);
            var material = renderer.sharedMaterial;
            Assert.That(material.shader.name, Is.EqualTo("Universal Render Pipeline/Lit"));
            Assert.That(material.GetFloat("_Metallic"), Is.EqualTo(renderer.name == "drainage" ? .8f : 0f));
            Assert.That(material.GetFloat("_Smoothness"), Is.EqualTo(isRoof ? .8f : .5f));
            if (renderer.name == "wood")
            {
                Assert.That(material.GetTexture("_BaseMap").name, Is.EqualTo("natural_wood_07_color_contrast_2p0"));
                Assert.That(material.GetTexture("_BumpMap"), Is.Null);
                Assert.That(material.GetTexture("_OcclusionMap"), Is.Null);
            }
            else if (isRoof)
            {
                Assert.That(material, Is.SameAs(AssetDatabase.LoadAssetAtPath<Material>(RoofCanopyPlexiglassSetup.MaterialPath)));
                Assert.That(material.GetFloat("_Surface"), Is.EqualTo(1f));
                Assert.That(material.GetTexture("_BumpMap"), Is.Null);
            }
            else
            {
                Assert.That(material.GetTexture("_BaseMap"), Is.Not.Null);
                Assert.That(material.GetTexture("_BumpMap"), Is.Not.Null);
                Assert.That(material, Is.SameAs(AssetDatabase.LoadAssetAtPath<Material>(RoofCanopyLightingSetup.AluminiumPath)));
            }
        }
    }
}

/// <summary>Editor-only paired renders. Never saves the scene or material changes.</summary>
public static class RoofCanopyLightingPreview
{
    private static string _output;
    private static Light _light;
    private static Light _fill;
    private static Texture _environment;
    private static Material[] _previewMaterials;
    private static Camera _camera;
    private static GameObject _content;
    private static Bounds _bounds;
    private static int _ticks;
    private static int _stage;

    public static GameObject CreateContent()
    {
        var root = new GameObject("Lighting Preview Board");
        typeof(ARContentFactory).GetMethod("CreateRoofCanopy", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { root.transform, 2 });
        return root;
    }

    public static void Run()
    {
        try
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, "-lightingOutput");
            _output = index >= 0 ? args[index + 1] : Path.GetFullPath("Temp/RoofCanopyLighting");
            Directory.CreateDirectory(_output);
            EditorSceneManager.OpenScene("Assets/Scenes/DroneView.unity", OpenSceneMode.Single);
            foreach (var canvas in Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None)) canvas.enabled = false;
            foreach (var camera in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None)) camera.enabled = false;
            _light = RenderSettings.sun;
            _fill = Object.FindObjectsByType<Light>(FindObjectsSortMode.None).Single(l => l != _light);
            _environment = RenderSettings.customReflectionTexture;
            _content = CreateContent();
            var metalRenderers = _content.GetComponentsInChildren<Renderer>().Where(r => r.name == "drainage").ToArray();
            _previewMaterials = metalRenderers.Select(r => new Material(r.sharedMaterial)).ToArray();
            for (var i = 0; i < metalRenderers.Length; i++) metalRenderers[i].sharedMaterial = _previewMaterials[i];
            var renderers = _content.GetComponentsInChildren<Renderer>();
            _bounds = renderers[0].bounds;
            foreach (var renderer in renderers.Skip(1)) _bounds.Encapsulate(renderer.bounds);
            _camera = new GameObject("Lighting Preview Camera", typeof(Camera), typeof(UniversalAdditionalCameraData)).GetComponent<Camera>();
            _camera.enabled = false;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.21f, 0.23f, 0.25f);
            _camera.cullingMask = 1 << 2;
            _camera.fieldOfView = 38f;
            _camera.nearClipPlane = 0.05f;
            _camera.farClipPlane = 35f;
            _camera.allowHDR = false;
            _camera.useOcclusionCulling = false;
            var data = _camera.GetUniversalAdditionalCameraData();
            data.renderShadows = true;
            data.renderPostProcessing = false;
            SetBeforeLighting();
            _ticks = 0;
            _stage = 0;
            EditorApplication.update += Tick;
        }
        catch (Exception exception) { Fail(exception); }
    }

    private static void SetBeforeLighting()
    {
        _light.enabled = true;
        _light.shadowStrength = .85f;
        _fill.enabled = false;
        RenderSettings.sun = _light;
        SetFlatAmbient(new Color(.28f, .30f, .34f));
        RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
        RenderSettings.customReflectionTexture = null;
        foreach (var material in _previewMaterials) material.SetFloat("_Metallic", 0f);
    }

    private static void SetAfterLighting()
    {
        _light.enabled = true;
        _light.shadowStrength = .55f;
        _fill.enabled = true;
        RenderSettings.sun = _light;
        SetFlatAmbient(new Color(.40f, .42f, .46f));
        RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
        RenderSettings.customReflectionTexture = _environment;
        foreach (var material in _previewMaterials) material.SetFloat("_Metallic", .8f);
    }

    private static void SetFlatAmbient(Color color)
    {
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = color;
        var probe = new SphericalHarmonicsL2();
        probe.AddAmbientLight(color.linear);
        RenderSettings.ambientProbe = probe;
    }

    private static void Tick()
    {
        if (++_ticks < 30) return;
        _ticks = 0;
        try
        {
            if (_stage == 0)
            {
                CaptureViews("before");
                SetAfterLighting();
                _stage = 1;
            }
            else
            {
                CaptureViews("after");
                // A no-shadow control proves that the after view contains actual shadows.
                _light.shadows = LightShadows.None;
                CaptureViews("after-no-shadow-control");
                _light.shadows = LightShadows.Hard;
                File.WriteAllText(Path.Combine(_output, "preview-settings.txt"),
                    $"Pipeline={GraphicsSettings.currentRenderPipeline.name}\n" +
                    $"Bounds={_bounds}\nLight Euler={_light.transform.eulerAngles}\n" +
                    $"Light direction={_light.transform.forward}\nIntensity={_light.intensity}\n" +
                    $"Ambient={RenderSettings.ambientMode} {RenderSettings.ambientLight}\n" +
                    "Same production factory, transforms, meshes, materials, camera pose and background in each pair.\n" +
                    "Before restores Step 3C.1 lighting, default skybox reflection and metallic=0 in memory.\n");
                EditorApplication.update -= Tick;
                Object.DestroyImmediate(_content);
                Object.DestroyImmediate(_camera.gameObject);
                foreach (var material in _previewMaterials) Object.DestroyImmediate(material);
                EditorApplication.Exit(0);
            }
        }
        catch (Exception exception) { Fail(exception); }
    }

    private static void CaptureViews(string label)
    {
        Capture(label + "-structure", new Vector3(-5.5f, -7f, 0.4f));
        Capture(label + "-roof", new Vector3(-5.5f, -7f, 4f));
    }

    private static void Capture(string name, Vector3 cameraOffset)
    {
        _camera.transform.position = _bounds.center + cameraOffset;
        _camera.transform.rotation = Quaternion.LookRotation(_bounds.center - _camera.transform.position, Vector3.forward);
        var target = RenderTexture.GetTemporary(1600, 1000, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        var previous = RenderTexture.active;
        var image = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
        try
        {
            RenderPipeline.SubmitRenderRequest(_camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
            RenderTexture.active = target;
            image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            image.Apply();
            File.WriteAllBytes(Path.Combine(_output, name + ".png"), image.EncodeToPNG());
            Debug.Log($"ROOFCANOPY_LIGHTING_PREVIEW path={Path.Combine(_output, name + ".png")}");
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
            Object.DestroyImmediate(image);
        }
    }

    private static void Fail(Exception exception)
    {
        EditorApplication.update -= Tick;
        Debug.LogException(exception);
        EditorApplication.Exit(1);
    }
}
