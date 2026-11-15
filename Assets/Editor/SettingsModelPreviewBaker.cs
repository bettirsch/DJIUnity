using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

public static class SettingsModelPreviewBaker
{
    private const string AssetDirectory = "Assets/Resources/SettingsModelPreviews";
    private const int PreviewWidth = 512;
    private const int PreviewHeight = 256;
    private const int PreviewLayer = 31;

    [MenuItem("Tools/RoofCanopy/Bake Settings Model Previews")]
    public static void Bake()
    {
        var mobile = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>("Assets/Settings/Mobile_RPAsset.asset");
        if (GraphicsSettings.currentRenderPipeline != mobile)
            throw new InvalidOperationException("Use the project's Mobile quality profile to bake Android model previews.");
        var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/Settings/Mobile_Renderer.asset");
        var video = renderer.rendererFeatures.OfType<DJIBackgroundURPFeature>().Single();
        var videoWasActive = video.isActive;
        var previous = SceneManager.GetActiveScene();
        if (!Application.isBatchMode && string.IsNullOrEmpty(previous.path))
            throw new InvalidOperationException("Save or open a scene before baking the model previews.");
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,
            Application.isBatchMode ? NewSceneMode.Single : NewSceneMode.Additive);
        SceneManager.SetActiveScene(scene);
        video.SetActive(false);
        try
        {
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(.48f, .54f, .6f);
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
            RenderSettings.customReflectionTexture = Resources.Load<Texture>("ARContent/RoofAssets/NeutralCanopyEnvironment");
            AddLight(1.25f, new Vector3(.3f, .5f, -1f));
            AddLight(.7f, new Vector3(-.6f, -.8f, -.5f));
            Directory.CreateDirectory(AssetDirectory);
            BakeModel(ARContentType.Cube, "Cube");
            BakeModel(ARContentType.RoofCanopy, "RoofCanopy");

            var args = Environment.GetCommandLineArgs();
            var output = Array.IndexOf(args, "-settingsPreviewOutput");
            if (output >= 0 && output + 1 < args.Length)
                CaptureMenu(Path.GetFullPath(args[output + 1]));
        }
        finally
        {
            video.SetActive(videoWasActive);
            if (previous.IsValid() && previous.isLoaded)
            {
                SceneManager.SetActiveScene(previous);
                EditorSceneManager.CloseScene(scene, true);
            }
        }
    }

    private static void AddLight(float intensity, Vector3 direction)
    {
        var light = new GameObject("Model preview light", typeof(Light)).GetComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = intensity;
        light.cullingMask = 1 << PreviewLayer;
        light.transform.rotation = Quaternion.LookRotation(direction);
        if (RenderSettings.sun == null) RenderSettings.sun = light;
    }

    private static Camera CreateCamera()
    {
        var camera = new GameObject("Settings preview camera", typeof(Camera), typeof(UniversalAdditionalCameraData)).GetComponent<Camera>();
        camera.enabled = false;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color32(30, 58, 70, 255);
        camera.allowHDR = false;
        camera.cullingMask = 1 << PreviewLayer;
        camera.useOcclusionCulling = false;
        camera.GetUniversalAdditionalCameraData().renderPostProcessing = false;
        return camera;
    }

    private static void BakeModel(ARContentType type, string name)
    {
        var root = new GameObject("Settings preview board");
        Camera camera = null;
        Camera overlay = null;
        Material ownedMaterial = null;
        try
        {
            var content = ARContentFactory.Create(type, root.transform, PreviewLayer);
            if (type == ARContentType.Cube)
                ownedMaterial = content.GetComponent<Renderer>().sharedMaterial;
            else if (content.name != "RoofCanopyPlacement")
                throw new InvalidOperationException("The RoofCanopy resource could not be loaded.");
            var renderers = content.GetComponentsInChildren<Renderer>();
            var bounds = renderers[0].bounds;
            foreach (var renderer in renderers)
                bounds.Encapsulate(renderer.bounds);

            camera = CreateCamera();
            camera.cullingMask = 0;
            overlay = CreateCamera();
            overlay.enabled = true;
            overlay.gameObject.AddComponent<DjiOverlayCullingParity>();
            overlay.GetUniversalAdditionalCameraData().renderType = CameraRenderType.Overlay;
            camera.GetUniversalAdditionalCameraData().cameraStack.Add(overlay);
            overlay.fieldOfView = 34f;
            overlay.aspect = (float)PreviewWidth / PreviewHeight;
            Frame(overlay, bounds);
            var asset = $"{AssetDirectory}/{name}.png";
            Capture(camera, PreviewWidth, PreviewHeight, asset);
            AssetDatabase.ImportAsset(asset, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(asset);
            importer.mipmapEnabled = false;
            importer.isReadable = false;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.SaveAndReimport();
            Debug.Log($"SETTINGS_MODEL_PREVIEW type={type} source={content.name} renderers={renderers.Length} asset={asset}");
        }
        finally
        {
            if (overlay != null) Object.DestroyImmediate(overlay.gameObject);
            if (camera != null) Object.DestroyImmediate(camera.gameObject);
            Object.DestroyImmediate(root);
            if (ownedMaterial != null) Object.DestroyImmediate(ownedMaterial);
        }
    }

    private static void Frame(Camera camera, Bounds bounds)
    {
        var direction = new Vector3(.57f, -1f, .62f).normalized;
        var rotation = Quaternion.LookRotation(-direction, -Vector3.forward);
        var inverse = Quaternion.Inverse(rotation);
        var vertical = Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * .5f) * .83f;
        var horizontal = vertical * camera.aspect;
        var distance = 0f;
        var extents = bounds.extents;
        for (var x = -1; x <= 1; x += 2)
        for (var y = -1; y <= 1; y += 2)
        for (var z = -1; z <= 1; z += 2)
        {
            var corner = inverse * Vector3.Scale(extents, new Vector3(x, y, z));
            distance = Mathf.Max(distance, Mathf.Abs(corner.x) / horizontal - corner.z);
            distance = Mathf.Max(distance, Mathf.Abs(corner.y) / vertical - corner.z);
        }
        var position = bounds.center + direction * distance;
        camera.transform.SetPositionAndRotation(position, rotation);
        camera.nearClipPlane = Mathf.Max(.001f, distance * .01f);
        camera.farClipPlane = distance + 3f * extents.magnitude;
        camera.worldToCameraMatrix = DjiSharedReferenceCubeOverlay.NativeOpticalToUnityViewBasis *
            ReferenceFrameTransforms.MatrixFromPose(ReferenceFrameTransforms.Invert(new Pose(position, rotation)));
        camera.projectionMatrix = Matrix4x4.Perspective(camera.fieldOfView, camera.aspect, camera.nearClipPlane, camera.farClipPlane);
        camera.cullingMatrix = camera.projectionMatrix * camera.worldToCameraMatrix;
    }

    private static void CaptureMenu(string directory)
    {
        Directory.CreateDirectory(directory);
        CaptureMenu(directory, ARContentType.Cube, "settings-menu-cube.png");
        CaptureMenu(directory, ARContentType.RoofCanopy, "settings-menu-canopy.png");
    }

    private static void CaptureMenu(string directory, ARContentType type, string name)
    {
        // Render the actual uGUI hierarchy in an isolated editor-only camera.
        const int width = 1560;
        const int height = 720;
        var root = new GameObject("Settings menu preview");
        var camera = CreateCamera();
        try
        {
            var controller = root.AddComponent<SettingsSceneController>();
            typeof(SettingsSceneController).GetMethod("BuildUi", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(controller, new object[] { type });
            var canvas = root.GetComponentInChildren<Canvas>();
            canvas.GetComponent<CanvasScaler>().enabled = false;
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = camera;
            canvas.planeDistance = 1f;
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                transform.gameObject.layer = PreviewLayer;
            var safe = (RectTransform)canvas.transform.Find("Safe Area");
            safe.anchorMin = Vector2.zero;
            safe.anchorMax = Vector2.one;
            safe.offsetMin = safe.offsetMax = Vector2.zero;
            Canvas.ForceUpdateCanvases();

            camera.backgroundColor = Color.black;
            camera.orthographic = true;
            camera.orthographicSize = .5f;
            camera.aspect = (float)width / height;
            camera.transform.SetPositionAndRotation(new Vector3(0, 0, -2), Quaternion.identity);
            camera.nearClipPlane = .01f;
            camera.farClipPlane = 10f;
            Capture(camera, width, height, Path.Combine(directory, name));
        }
        finally
        {
            Object.DestroyImmediate(root);
            Object.DestroyImmediate(camera.gameObject);
        }
    }

    private static void Capture(Camera camera, int width, int height, string path)
    {
        var target = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        var previous = RenderTexture.active;
        var image = new Texture2D(width, height, TextureFormat.RGB24, false);
        try
        {
            camera.targetTexture = target;
            Canvas.ForceUpdateCanvases();
            RenderPipeline.SubmitRenderRequest(camera, new RenderPipeline.StandardRequest { destination = target });
            RenderTexture.active = target;
            image.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
            image.Apply();
            File.WriteAllBytes(path, image.EncodeToPNG());
        }
        finally
        {
            camera.targetTexture = null;
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
            Object.DestroyImmediate(image);
        }
    }
}
