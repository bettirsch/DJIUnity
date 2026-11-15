using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

/// <summary>Explicit Editor authoring of the matched Step 3C.2 candidate; no runtime updater.</summary>
public static class RoofCanopyLightingSetup
{
    public const string EnvironmentPath = "Assets/Resources/ARContent/RoofAssets/NeutralCanopyEnvironment.cubemap";
    public const string AluminiumPath = "Assets/Resources/ARContent/RoofAssets/materials/Aluminium scratches.mat";
    private const string PrefabPath = "Assets/Resources/ARContent/RoofAssets/roof_canopy1.prefab";

    public static void ApplyValidatedCandidate()
    {
        var scene = EditorSceneManager.OpenScene("Assets/Scenes/DroneView.unity", OpenSceneMode.Single);
        var key = Object.FindObjectsByType<Light>(FindObjectsSortMode.None)
            .Single(l => l.gameObject.scene == scene && l == RenderSettings.sun);
        key.shadowStrength = 0.55f;
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.40f, 0.42f, 0.46f);
        var probe = new SphericalHarmonicsL2();
        probe.AddAmbientLight(RenderSettings.ambientLight.linear);
        RenderSettings.ambientProbe = probe;

        var fillObject = GameObject.Find("DroneView Content Fill Light");
        var fill = fillObject != null ? fillObject.GetComponent<Light>() :
            new GameObject("DroneView Content Fill Light").AddComponent<Light>();
        fill.type = LightType.Directional;
        fill.intensity = 0.25f;
        fill.color = new Color(0.86f, 0.91f, 1f);
        fill.shadows = LightShadows.None;
        fill.cullingMask = 1 << 2;
        // Canopy vertical is board-local +Z; fill rays should travel downward.
        fill.transform.rotation = Quaternion.LookRotation(new Vector3(-0.65f, 0.50f, -0.25f), Vector3.forward);
        var lightData = fill.GetComponent<UniversalAdditionalLightData>() ?? fill.gameObject.AddComponent<UniversalAdditionalLightData>();
        lightData.usePipelineSettings = false;

        var environment = AssetDatabase.LoadAssetAtPath<Cubemap>(EnvironmentPath);
        if (environment == null)
        {
            environment = CreateNeutralEnvironment();
            AssetDatabase.CreateAsset(environment, EnvironmentPath);
        }
        RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
        RenderSettings.customReflectionTexture = environment;
        RenderSettings.reflectionIntensity = 1f;

        var prefab = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            var metalRenderers = prefab.GetComponentsInChildren<Renderer>().Where(r => r.name == "drainage").ToArray();
            if (metalRenderers.Length != 1) throw new InvalidOperationException("Expected one drainage metal renderer; the acrylic roof is independent.");
            var material = AssetDatabase.LoadAssetAtPath<Material>(AluminiumPath);
            if (material == null)
            {
                material = new Material(metalRenderers[0].sharedMaterial) { name = "Aluminium scratches" };
                AssetDatabase.CreateAsset(material, AluminiumPath);
            }
            material.SetFloat("_Metallic", 0.8f);
            material.SetFloat("_Smoothness", 0.5f);
            EditorUtility.SetDirty(material);
            foreach (var renderer in metalRenderers) renderer.sharedMaterial = material;
            PrefabUtility.SaveAsPrefabAsset(prefab, PrefabPath);
        }
        finally { PrefabUtility.UnloadPrefabContents(prefab); }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log("ROOFCANOPY_STEP3C2_AUTHORED key=.85 shadow=.55 fill=.25 ambient=.40,.42,.46 metal=.8 smooth=.5");
    }

    private static Cubemap CreateNeutralEnvironment()
    {
        // Same smooth neutral radiance gradient and mip chain as the audited
        // candidate. It is synthetic fixed fill, not a measured DJI environment.
        var cube = new Cubemap(64, TextureFormat.RGBAHalf, true) { name = "NeutralCanopyEnvironment" };
        for (var f = 0; f < 6; f++)
        {
            var pixels = new Color[64 * 64];
            for (var y = 0; y < 64; y++) for (var x = 0; x < 64; x++)
            {
                var u = (x + 0.5f) / 32f - 1f;
                var v = (y + 0.5f) / 32f - 1f;
                var direction = (CubemapFace)f switch
                {
                    CubemapFace.PositiveX => new Vector3(1, -v, -u),
                    CubemapFace.NegativeX => new Vector3(-1, -v, u),
                    CubemapFace.PositiveY => new Vector3(u, 1, v),
                    CubemapFace.NegativeY => new Vector3(u, -1, -v),
                    CubemapFace.PositiveZ => new Vector3(u, -v, 1),
                    _ => new Vector3(-u, -v, -1)
                };
                var h = direction.normalized.z * 0.5f + 0.5f;
                pixels[y * 64 + x] = Color.Lerp(new Color(.09f, .10f, .12f), new Color(.65f, .68f, .72f), h);
            }
            cube.SetPixels(pixels, (CubemapFace)f);
        }
        cube.Apply(true, false);
        return cube;
    }
}
