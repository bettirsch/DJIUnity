using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Rendering.Universal.ShaderGUI;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

/// <summary>Explicit material authoring/preview only. No runtime lighting or geometry changes.</summary>
public static class RoofCanopyPlexiglassSetup
{
    public const string MaterialPath = "Assets/Resources/ARContent/RoofAssets/materials/Roof Plexiglass.mat";
    public const float SelectedAlpha = 0.45f;
    private const string PrefabPath = "Assets/Resources/ARContent/RoofAssets/roof_canopy1.prefab";
    private static string _output;
    private static GameObject _content;
    private static Camera _camera;
    private static MeshRenderer _roof;
    private static Material _glass;
    private static Bounds _bounds;
    private static int _ticks;
    private static int _stage;
    private static ScriptableRendererFeature _backgroundFeature;
    private static bool _backgroundFeatureWasActive;
    private static readonly float[] PreviewAlphas = { .30f, .40f, .50f, .60f, SelectedAlpha };

    public static Material CreateMaterial(float alpha)
    {
        var material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "Roof Plexiglass" };
        ConfigureMaterial(material, alpha);
        return material;
    }

    private static void ConfigureMaterial(Material material, float alpha)
    {
        material.SetFloat("_WorkflowMode", 1f);
        material.SetFloat("_Surface", 1f);
        material.SetFloat("_Blend", 0f);
        // Ordinary alpha blending also attenuates highlights, avoiding the
        // opaque-white appearance of preserved specular on the closed sheet.
        material.SetFloat("_BlendModePreserveSpecular", 0f);
        material.SetFloat("_AlphaClip", 0f);
        material.SetFloat("_Cull", (float)CullMode.Off);
        material.SetFloat("_Metallic", 0f);
        material.SetFloat("_Smoothness", .8f);
        material.SetColor("_BaseColor", new Color(.85f, .92f, .96f, alpha));
        material.SetFloat("_ReceiveShadows", 1f);
        material.SetFloat("_SpecularHighlights", 1f);
        material.SetFloat("_EnvironmentReflections", 1f);
        material.SetFloat("_QueueOffset", 0f);
        material.doubleSidedGI = true;
        BaseShaderGUI.SetMaterialKeywords(material, LitGUI.SetMaterialKeywords);
    }

    public static void ApplySelectedMaterial()
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (material == null)
        {
            material = CreateMaterial(SelectedAlpha);
            AssetDatabase.CreateAsset(material, MaterialPath);
        }
        else ConfigureMaterial(material, SelectedAlpha);
        EditorUtility.SetDirty(material);
        var prefab = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            var roof = prefab.GetComponentsInChildren<MeshRenderer>().Single(r => r.name == "roof");
            roof.sharedMaterial = material;
            roof.shadowCastingMode = ShadowCastingMode.Off;
            roof.receiveShadows = true;
            PrefabUtility.SaveAsPrefabAsset(prefab, PrefabPath);
        }
        finally { PrefabUtility.UnloadPrefabContents(prefab); }
        AssetDatabase.SaveAssets();
        Debug.Log($"ROOF_PLEXIGLASS_AUTHORED alpha={SelectedAlpha} queue={material.renderQueue} cull=Off zwrite={material.GetFloat("_ZWrite")} shadows=Off");
    }

    public static void RunPreviews()
    {
        try
        {
            var args = Environment.GetCommandLineArgs();
            _output = args[Array.IndexOf(args, "-previewOutput") + 1];
            Directory.CreateDirectory(_output);
            EditorSceneManager.OpenScene("Assets/Scenes/DroneView.unity", OpenSceneMode.Single);
            QualitySettings.SetQualityLevel(0, false);
            // There is no OES feed in an offline capture. Exclude only its
            // otherwise-no-op graph pass from this synthetic-background preview.
            // The feature is restored in memory and its asset is never saved.
            _backgroundFeature = AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/Settings/Mobile_Renderer.asset")
                .rendererFeatures.Single(f => f is DJIBackgroundURPFeature);
            _backgroundFeatureWasActive = _backgroundFeature.isActive;
            _backgroundFeature.SetActive(false);
            foreach (var c in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None)) c.enabled = false;
            foreach (var c in Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None)) c.enabled = false;
            _content = RoofCanopyLightingPreview.CreateContent();
            var renderers = _content.GetComponentsInChildren<Renderer>();
            _roof = renderers.OfType<MeshRenderer>().Single(r => r.name == "roof");
            _bounds = renderers[0].bounds;
            foreach (var renderer in renderers.Skip(1)) _bounds.Encapsulate(renderer.bounds);
            _glass = CreateMaterial(SelectedAlpha);
            _camera = new GameObject("Plexiglass preview camera", typeof(Camera), typeof(UniversalAdditionalCameraData)).GetComponent<Camera>();
            _camera.enabled = false;
            _camera.cullingMask = 1 << 2;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(.21f, .23f, .25f);
            _camera.fieldOfView = 38f;
            _camera.nearClipPlane = .05f;
            _camera.farClipPlane = 35f;
            _camera.allowHDR = false;
            _camera.useOcclusionCulling = false;
            _camera.GetUniversalAdditionalCameraData().renderPostProcessing = false;
            _camera.GetUniversalAdditionalCameraData().renderShadows = true;
            _ticks = 0;
            _stage = 0;
            EditorApplication.update += Tick;
        }
        catch (Exception e) { Fail(e); }
    }

    private static void Tick()
    {
        if (++_ticks < 25) return;
        _ticks = 0;
        try
        {
            string label;
            if (_stage == 0)
            {
                _roof.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(RoofCanopyLightingSetup.AluminiumPath);
                _roof.shadowCastingMode = ShadowCastingMode.On;
                label = "before-aluminium";
            }
            else
            {
                var alpha = PreviewAlphas[Math.Min(_stage - 1, PreviewAlphas.Length - 1)];
                _glass.SetColor("_BaseColor", new Color(.85f, .92f, .96f, alpha));
                _roof.sharedMaterial = _glass;
                _roof.shadowCastingMode = ShadowCastingMode.Off;
                _roof.receiveShadows = true;
                label = _stage <= PreviewAlphas.Length ? $"alpha-{Mathf.RoundToInt(alpha * 100):D2}" : "repeat-selected";
            }
            CaptureViews(label);
            if (_stage == PreviewAlphas.Length)
            {
                _roof.enabled = false;
                Capture("roof-hidden-roof", new Vector3(-5.5f, -7f, 4f));
                _roof.enabled = true;
                _camera.backgroundColor = new Color(.65f, .45f, .25f);
                Capture("selected-warm-background", new Vector3(-5.5f, -7f, 4f));
                _camera.backgroundColor = new Color(.15f, .45f, .65f);
                Capture("selected-cool-background", new Vector3(-5.5f, -7f, 4f));
                _camera.backgroundColor = new Color(.21f, .23f, .25f);
            }
            if (++_stage <= PreviewAlphas.Length + 1) return;
            var metal = _content.GetComponentsInChildren<Renderer>().Single(r => r.name == "drainage").sharedMaterial;
            File.WriteAllText(Path.Combine(_output, "preview-settings.txt"),
                $"Pipeline={GraphicsSettings.currentRenderPipeline.name}\nColorSpace={QualitySettings.activeColorSpace}\n" +
                $"Bounds={_bounds}\nShader={_glass.shader.name}\nSurface={_glass.GetFloat("_Surface")} Blend={_glass.GetFloat("_Blend")}\n" +
                $"Queue={_glass.renderQueue} ZWrite={_glass.GetFloat("_ZWrite")} Cull={_glass.GetFloat("_Cull")}\n" +
                $"SrcBlend={_glass.GetFloat("_SrcBlend")} DstBlend={_glass.GetFloat("_DstBlend")} PreserveSpecular={_glass.GetFloat("_BlendModePreserveSpecular")}\n" +
                $"Drainage={metal.name} metallic={metal.GetFloat("_Metallic")} smoothness={metal.GetFloat("_Smoothness")}\n" +
                "Scene lighting, reflection, camera pose and model transforms unchanged in every pair.\n" +
                "Before has original aluminium roof/casts shadows; variants have only roof material/cast-shadows change.\n" +
                "Solid-background controls are compositing checks, not a live OES/device test.\n");
            _backgroundFeature.SetActive(_backgroundFeatureWasActive);
            EditorApplication.update -= Tick;
            Object.DestroyImmediate(_content);
            Object.DestroyImmediate(_camera.gameObject);
            Object.DestroyImmediate(_glass);
            EditorApplication.Exit(0);
        }
        catch (Exception e) { Fail(e); }
    }

    private static void CaptureViews(string label)
    {
        Capture(label + "-structure", new Vector3(-5.5f, -7f, .4f));
        Capture(label + "-roof", new Vector3(-5.5f, -7f, 4f));
        Capture(label + "-underside", new Vector3(-3.5f, -4f, -2f));
        Capture(label + "-opposite", new Vector3(5.5f, 6f, 3f));
        Capture(label + "-grazing", new Vector3(-5.5f, -7f, 1.4f));
    }

    private static void Capture(string label, Vector3 offset)
    {
        _camera.transform.position = _bounds.center + offset;
        _camera.transform.rotation = Quaternion.LookRotation(_bounds.center - _camera.transform.position, Vector3.forward);
        var target = RenderTexture.GetTemporary(1600, 1000, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        var previous = RenderTexture.active;
        var image = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
        try
        {
            // Use the complete URP frame lifecycle (also used for camera stacks),
            // rather than reusing deferred buffers across standalone camera requests.
            RenderPipeline.SubmitRenderRequest(_camera, new RenderPipeline.StandardRequest { destination = target });
            RenderTexture.active = target;
            image.ReadPixels(new Rect(0,0,target.width,target.height),0,0); image.Apply();
            File.WriteAllBytes(Path.Combine(_output,label+".png"),image.EncodeToPNG());
            Debug.Log("ROOF_PLEXIGLASS_PREVIEW " + label);
        }
        finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target); Object.DestroyImmediate(image); }
    }

    private static void Fail(Exception e)
    {
        EditorApplication.update -= Tick;
        if (_backgroundFeature != null) _backgroundFeature.SetActive(_backgroundFeatureWasActive);
        Debug.LogException(e);
        EditorApplication.Exit(1);
    }
}
