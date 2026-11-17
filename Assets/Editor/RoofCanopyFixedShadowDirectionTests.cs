using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

public sealed class RoofCanopyFixedShadowDirectionTests
{
    private static readonly Vector3 RequiredRay = new Vector3(.409576f, .573576f, -.709406f).normalized;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private Scene _previous, _scene;

    [SetUp] public void Setup()
    {
        _previous = SceneManager.GetActiveScene();
        _scene = EditorSceneManager.OpenScene("Assets/Scenes/DroneView.unity", OpenSceneMode.Additive);
        SceneManager.SetActiveScene(_scene);
    }

    [TearDown] public void Cleanup()
    {
        if (_previous.IsValid() && _previous.isLoaded) SceneManager.SetActiveScene(_previous);
        if (_scene.IsValid() && _scene.isLoaded) EditorSceneManager.CloseScene(_scene, true);
    }

    [Test] public void SceneLightAndSerializedAndNewSettingsUseRequiredWorldRay()
    {
        var light = RenderSettings.sun;
        var settings = Resources.Load<RoofCanopyShadowSettings>("RoofCanopyShadowSettings");
        Assert.That(light.transform.parent, Is.Null);
        Assert.That(Vector3.Distance(light.transform.forward, RequiredRay), Is.LessThan(1e-6f));
        Assert.That(Vector3.Distance(settings.sunlightRayDirection, RequiredRay), Is.LessThan(1e-6f));
        Assert.That(settings.sunlightRayDirection.magnitude, Is.EqualTo(1).Within(1e-6f));
        var defaults = ScriptableObject.CreateInstance<RoofCanopyShadowSettings>();
        try { Assert.That(Vector3.Distance(defaults.sunlightRayDirection, RequiredRay), Is.LessThan(1e-6f)); }
        finally { Object.DestroyImmediate(defaults); }
        Assert.That(settings.wallEnabled || settings.wallCalibrated, Is.False);
        Assert.That(light.intensity, Is.EqualTo(.85f));
        Assert.That(light.shadowStrength, Is.EqualTo(.55f));
        Assert.That(light.color, Is.EqualTo(new Color(1, .97f, .92f)));
    }

    [Test] public void OnlyXReversesRelativeToBWithIdenticalElevationAndShadowLength()
    {
        var settings = Resources.Load<RoofCanopyShadowSettings>("RoofCanopyShadowSettings");
        var b = new Vector3(-.409576f, .573576f, -.709406f).normalized;
        var c = settings.sunlightRayDirection;
        Assert.That(c.x, Is.EqualTo(-b.x).Within(1e-6));
        Assert.That(c.y, Is.EqualTo(b.y).Within(1e-6));
        Assert.That(c.z, Is.EqualTo(b.z).Within(1e-6));
        foreach (float height in new[] { .5f, 1f, 2.2f, 2.32f })
        {
            var point = new Vector3(.7f, -.8f, height);
            Assert.That(ShadowProjectionMath.TryProject(point, b, settings.Ground, out var pb), Is.True);
            Assert.That(ShadowProjectionMath.TryProject(point, c, settings.Ground, out var pc), Is.True);
            var db = pb - point; var dc = pc - point;
            Assert.That(dc.x, Is.EqualTo(-db.x).Within(1e-6));
            Assert.That(dc.y, Is.EqualTo(db.y).Within(1e-6));
            Assert.That(dc.magnitude, Is.EqualTo(db.magnitude).Within(1e-6));
        }
    }

    [Test] public void NewCReceiverBakesDifferentMaskAndDoesNotReuseBTexture()
    {
        var settings = Object.Instantiate(Resources.Load<RoofCanopyShadowSettings>("RoofCanopyShadowSettings"));
        var board = new GameObject("Fresh shadow cache test");
        try
        {
            settings.resolution = 128;
            var c = settings.sunlightRayDirection;
            settings.sunlightRayDirection = new Vector3(-c.x, c.y, c.z);
            var oldMask = ShadowProjectionMath.Bake(settings, settings.Ground, out _, out _);
            var old = RoofCanopyProjectedShadows.Create(board.transform, 2, settings);
            var oldTexture = board.GetComponentInChildren<Renderer>(true).sharedMaterial.GetTexture("_ShadowMask");
            Object.DestroyImmediate(old);
            Assert.That(oldTexture == null, Is.True);
            settings.sunlightRayDirection = c;
            var currentMask = ShadowProjectionMath.Bake(settings, settings.Ground, out _, out _);
            Assert.That(currentMask.SequenceEqual(oldMask), Is.False);
            var current = RoofCanopyProjectedShadows.Create(board.transform, 2, settings);
            Assert.That(current.GeometryBuildCount, Is.EqualTo(1));
            Assert.That(board.GetComponentInChildren<Renderer>(true).sharedMaterial.GetTexture("_ShadowMask"), Is.Not.Null);
            Assert.That(current.WallVisible, Is.False);
        }
        finally { Object.DestroyImmediate(board); Object.DestroyImmediate(settings); }
    }

    // Synthetic camera/attitude inputs exercise the actual overlay application,
    // not a hardware flight or a new localization policy.
    [TestCase("CameraTranslation", 1f, 0f, 0f, "VISUAL")]
    [TestCase("CameraRotation", 0f, 20f, 10f, "VISUAL")]
    [TestCase("AircraftYaw", 0f, 45f, 0f, "TELEMETRY")]
    [TestCase("GimbalPitch", 0f, 0f, -35f, "TELEMETRY")]
    [TestCase("VisualToTelemetry", .3f, 15f, -10f, "TELEMETRY")]
    [TestCase("VisualReacquired", .2f, -20f, 5f, "REACQUIRED")]
    public void ProductionOverlayKeepsLightModelAndCachedGroundFixed(
        string scenario, float translation, float yaw, float pitch, string reason)
    {
        var selected = ARContentSelection.Selected;
        var video = new GameObject("Fixed shadow video", typeof(Camera));
        var providerObject = new GameObject("Fixed shadow provider");
        var host = new GameObject("Fixed shadow overlay");
        GameObject root = null, camera = null;
        try
        {
            ARContentSelection.Set(ARContentType.RoofCanopy);
            var provider = providerObject.AddComponent<DjiBoardVisionProvider>();
            var overlay = host.AddComponent<DjiSharedReferenceCubeOverlay>();
            overlay.Configure(video.GetComponent<Camera>(), provider);
            var type = typeof(DjiSharedReferenceCubeOverlay);
            root = (GameObject)type.GetField("_contentRoot", Private).GetValue(overlay);
            camera = ((Camera)type.GetField("_overlayCamera", Private).GetValue(overlay)).gameObject;
            type.GetField("_hasAuthoritativeVisualAcquisition", Private).SetValue(overlay, true);
            var apply = type.GetMethod("ApplyStandaloneBoardOverlayPose", Private);
            var visible = type.GetMethod("SetCubeVisible", Private);
            var baseline = new Pose(new Vector3(0, 0, 2), Quaternion.identity);
            apply.Invoke(overlay, new object[] { baseline });
            visible.Invoke(overlay, new object[] { true, "VISUAL" });
            var shadow = root.GetComponent<RoofCanopyProjectedShadows>();
            var ground = root.GetComponentsInChildren<MeshFilter>(true).Single(f => f.name.Contains("Ground Shadow"));
            var vertices = ground.sharedMesh.vertices.Select(ground.transform.TransformPoint).ToArray();
            var model = root.GetComponentsInChildren<Transform>(true).Single(t => t.name == "BoardRoofCanopy");
            var modelMatrix = model.localToWorldMatrix;
            var lightMatrix = RenderSettings.sun.transform.localToWorldMatrix;
            var settings = Resources.Load<RoofCanopyShadowSettings>("RoofCanopyShadowSettings");
            var ray = settings.sunlightRayDirection;
            var attitude = Quaternion.AngleAxis(yaw, Vector3.forward) * Quaternion.AngleAxis(pitch, Vector3.right);
            var propagated = ReferenceFrameTransforms.PredictCameraFromBoardRotationOnly(baseline, Quaternion.identity, attitude);
            propagated.position += new Vector3(translation, -.5f * translation, 0);
            apply.Invoke(overlay, new object[] { propagated });
            visible.Invoke(overlay, new object[] { true, reason });
            Assert.That(root.transform.localToWorldMatrix, Is.EqualTo(Matrix4x4.identity), scenario);
            Assert.That(model.localToWorldMatrix, Is.EqualTo(modelMatrix), scenario);
            Assert.That(RenderSettings.sun.transform.localToWorldMatrix, Is.EqualTo(lightMatrix), scenario);
            Assert.That(settings.sunlightRayDirection, Is.EqualTo(ray), scenario);
            Assert.That(ground.sharedMesh.vertices.Select(ground.transform.TransformPoint).ToArray(), Is.EqualTo(vertices), scenario);
            Assert.That(shadow.GroundVisible, Is.True, scenario);
            Assert.That(shadow.GeometryBuildCount, Is.EqualTo(1), scenario);
            Assert.That(shadow.WallVisible, Is.False, scenario);
        }
        finally
        {
            if (root != null) Object.DestroyImmediate(root);
            if (camera != null) Object.DestroyImmediate(camera);
            Object.DestroyImmediate(host); Object.DestroyImmediate(providerObject); Object.DestroyImmediate(video);
            ARContentSelection.Set(selected);
        }
    }
}
