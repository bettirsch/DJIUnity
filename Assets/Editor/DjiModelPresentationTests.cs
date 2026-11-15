using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

public sealed class DjiModelPresentationTests
{
    private GameObject _providerObject, _videoObject, _overlayObject, _controlsObject, _statusObject, _worldObject;
    private DjiBoardVisionProvider _provider;
    private DjiSharedReferenceCubeOverlay _overlay;
    private DjiModelPresentationControls _controls;
    private DjiTrackingStatusIndicator _status;
    private DjiWorldLocalizationShadow _world;
    private ARContentType _selection;

    [SetUp]
    public void SetUp()
    {
        _selection = ARContentSelection.Selected;
        ARContentSelection.Set(ARContentType.RoofCanopy);
        _providerObject = new GameObject("Presentation provider");
        _provider = _providerObject.AddComponent<DjiBoardVisionProvider>();
        _videoObject = new GameObject("Presentation video", typeof(Camera));
        _overlayObject = new GameObject("Presentation overlay");
        _overlay = _overlayObject.AddComponent<DjiSharedReferenceCubeOverlay>();
        _overlay.Configure(_videoObject.GetComponent<Camera>(), _provider, true);
        _controlsObject = new GameObject("Presentation controls");
        _controls = _controlsObject.AddComponent<DjiModelPresentationControls>();
        _controls.Configure(_overlay);
        _worldObject = new GameObject("Presentation world");
        _world = _worldObject.AddComponent<DjiWorldLocalizationShadow>();
        _statusObject = new GameObject("Presentation status");
        _status = _statusObject.AddComponent<DjiTrackingStatusIndicator>();
        _status.Configure(_overlay, _world);
    }

    [TearDown]
    public void TearDown()
    {
        var root = Get<GameObject>(_overlay, "_contentRoot");
        var camera = Get<Camera>(_overlay, "_overlayCamera");
        Object.DestroyImmediate(_controlsObject); Object.DestroyImmediate(_statusObject);
        Object.DestroyImmediate(_overlayObject);
        if (root != null) Object.DestroyImmediate(root);
        if (camera != null) Object.DestroyImmediate(camera.gameObject);
        Object.DestroyImmediate(_providerObject); Object.DestroyImmediate(_videoObject); Object.DestroyImmediate(_worldObject);
        ARContentSelection.Set(_selection);
    }

    [Test]
    public void InitialPreferenceShown_DoesNotBypassFirstAcquisition()
    {
        Assert.That(_overlay.ModelVisibleByUser, Is.True);
        AssertVisibility(false);
        _controls.ToggleModelVisibility();
        Assert.That(Get<Text>(_controls, "_visibilityLabel").text, Is.EqualTo("SHOW MODEL"));
        AssertVisibility(false);
        _controls.ToggleModelVisibility();
        Invoke(_overlay, "SetCubeVisible", true, "UNACQUIRED_TEST");
        AssertVisibility(false);
        RefreshStatus();
        Assert.That(_status.DisplayedStatus, Is.EqualTo(DjiTrackingStatus.Waiting));
    }

    [TestCase(DjiRenderPoseSource.Visual, false, DjiTrackingStatus.Visual)]
    [TestCase(DjiRenderPoseSource.TelemetryPropagated, false, DjiTrackingStatus.TelemetryLimited)]
    [TestCase(DjiRenderPoseSource.TelemetryPropagated, true, DjiTrackingStatus.Telemetry)]
    public void HiddenModel_PreservesTrueTrackingStatus(DjiRenderPoseSource source, bool fresh, DjiTrackingStatus expected)
    {
        if (fresh) FreshTranslation();
        Applied(source);
        _controls.ToggleModelVisibility();
        RefreshStatus();
        AssertVisibility(false);
        Assert.That(_overlay.TrackingRenderPoseSource, Is.EqualTo(source));
        Assert.That(_overlay.DisplayedRenderPoseSource, Is.EqualTo(DjiRenderPoseSource.None));
        Assert.That(_status.DisplayedStatus, Is.EqualTo(expected));
        _controls.ToggleModelVisibility();
        AssertVisibility(true);
        Assert.That(_overlay.DisplayedRenderPoseSource, Is.EqualTo(source));
    }

    [Test]
    public void ActualLossWhileHidden_RemainsLostEvenAfterShow()
    {
        Applied(DjiRenderPoseSource.Visual);
        _controls.ToggleModelVisibility();
        Invoke(_overlay, "SetCubeVisible", false, "ACTUAL_LOSS");
        Set(_overlay, "_lastRenderPoseSource", DjiRenderPoseSource.None);
        RefreshStatus(); Assert.That(_status.DisplayedStatus, Is.EqualTo(DjiTrackingStatus.Lost));
        _controls.ToggleModelVisibility(); AssertVisibility(false);
        RefreshStatus(); Assert.That(_status.DisplayedStatus, Is.EqualTo(DjiTrackingStatus.Lost));
    }

    [Test]
    public void ReacquisitionWhileHidden_RestoresAuthorityButNotUserVisibility()
    {
        Applied(DjiRenderPoseSource.None, false);
        _overlay.SetModelVisibleByUser(false);
        var accepted = (Action<Pose, long>)Get<object>(_provider, "AuthoritativeVisualPoseAccepted");
        accepted?.Invoke(new Pose(Vector3.forward, Quaternion.identity), 123);
        Invoke(_overlay, "SetCubeVisible", true, "ACCEPTED_VISUAL");
        Set(_overlay, "_lastRenderPoseSource", DjiRenderPoseSource.Visual);
        RefreshStatus();
        Assert.That(_status.DisplayedStatus, Is.EqualTo(DjiTrackingStatus.Visual));
        AssertVisibility(false);
        _overlay.SetModelVisibleByUser(true);
        AssertVisibility(true);
    }

    [Test]
    public void LateUpdateWhileHidden_AppliesLatestVisualPose_ShowNeedsNoNewDetection()
    {
        var calibration = _providerObject.AddComponent<DjiCameraCalibration>();
        Invoke(calibration, "Awake");
        calibration.SetAllowProvisionalCalibrationForValidation(true);
        Set(_provider, "_calibration", calibration);
        Set(_provider, "<HasHighConfidenceCameraFromBoard>k__BackingField", true);
        Set(_provider, "<CurrentVisualPoseQuality>k__BackingField", DjiBoardVisionProvider.VisualPoseQuality.HighConfidence);
        Set(_provider, "_lastHighConfidenceCameraFromBoardRealtime", Time.realtimeSinceStartup);
        Set(_provider, "_currentHighConfidenceVisualTimestampNs", 123L);
        Set(_overlay, "_hasAuthoritativeVisualAcquisition", true);
        _overlay.SetModelVisibleByUser(false);
        var pose = new Pose(new Vector3(.2f, .1f, 5f), Quaternion.Euler(10, 15, 3));
        Set(_provider, "<CurrentHighConfidenceCameraFromBoard>k__BackingField", pose);
        Invoke(_overlay, "LateUpdate");
        var camera = Get<Camera>(_overlay, "_overlayCamera");
        var expected = DjiSharedReferenceCubeOverlay.NativeOpticalToUnityViewBasis * ReferenceFrameTransforms.MatrixFromPose(pose);
        Assert.That(camera.worldToCameraMatrix, Is.EqualTo(expected));
        Assert.That(_overlay.TrackingRenderPoseSource, Is.EqualTo(DjiRenderPoseSource.Visual));
        AssertVisibility(false);
        var before = camera.worldToCameraMatrix;
        _overlay.SetModelVisibleByUser(true);
        AssertVisibility(true);
        Assert.That(camera.worldToCameraMatrix, Is.EqualTo(before));
        Assert.That(_provider.CurrentHighConfidenceCameraFromBoard, Is.EqualTo(pose));
    }

    [Test]
    public void RapidToggles_LeaveInstancesMaterialsCamerasAndWorldUnchanged()
    {
        Applied(DjiRenderPoseSource.TelemetryPropagated);
        var root = Get<GameObject>(_overlay, "_contentRoot");
        var model = Get<GameObject>(_overlay, "_cube");
        var renderers = Get<Renderer[]>(_overlay, "_contentRenderers");
        var materials = renderers.Select(r => r.sharedMaterial).ToArray();
        var materialJson = materials.Select(UnityEditor.EditorJsonUtility.ToJson).ToArray();
        var matrix = model.transform.localToWorldMatrix;
        var snapshot = _world.Snapshot;
        var camera = Get<Camera>(_overlay, "_overlayCamera");
        var view = camera.worldToCameraMatrix; var projection = camera.projectionMatrix;
        var debugRenderers = root.GetComponentsInChildren<Renderer>().Except(renderers).ToArray();
        var debugEnabled = debugRenderers.Select(r => r.enabled).ToArray();
        for (int i = 0; i < 200; i++) _controls.ToggleModelVisibility();
        Assert.That(_overlay.ModelVisibleByUser, Is.True); AssertVisibility(true);
        Assert.That(Get<GameObject>(_overlay, "_cube"), Is.SameAs(model));
        Assert.That(root.GetComponentsInChildren<Renderer>().Length, Is.EqualTo(renderers.Length + debugRenderers.Length));
        Assert.That(renderers.Select(r => r.sharedMaterial), Is.EqualTo(materials));
        Assert.That(materials.Select(UnityEditor.EditorJsonUtility.ToJson), Is.EqualTo(materialJson));
        Assert.That(debugRenderers.Select(r => r.enabled), Is.EqualTo(debugEnabled));
        Assert.That(model.transform.localToWorldMatrix, Is.EqualTo(matrix));
        Assert.That(_world.Snapshot, Is.EqualTo(snapshot));
        Assert.That(camera.worldToCameraMatrix, Is.EqualTo(view)); Assert.That(camera.projectionMatrix, Is.EqualTo(projection));
        Assert.That(camera.enabled && _videoObject.GetComponent<Camera>().enabled && _provider.enabled && _world.enabled, Is.True);
    }

    [Test]
    public void ReconnectedCamera_DoesNotResetUserPreferenceOrDuplicateModel()
    {
        Applied(DjiRenderPoseSource.Visual); _overlay.SetModelVisibleByUser(false);
        var model = Get<GameObject>(_overlay, "_cube");
        _videoObject.GetComponent<Camera>().enabled = false;
        RefreshStatus(); Assert.That(_status.DisplayedStatus, Is.EqualTo(DjiTrackingStatus.Lost));
        _videoObject.GetComponent<Camera>().enabled = true;
        _overlay.Configure(_videoObject.GetComponent<Camera>(), _provider, true);
        Applied(DjiRenderPoseSource.TelemetryPropagated);
        Assert.That(_overlay.ModelVisibleByUser, Is.False); AssertVisibility(false);
        Assert.That(Get<GameObject>(_overlay, "_cube"), Is.SameAs(model));
        RefreshStatus(); Assert.That(_status.DisplayedStatus, Is.EqualTo(DjiTrackingStatus.TelemetryLimited));
        _overlay.SetModelVisibleByUser(true); AssertVisibility(true);
    }

    [Test]
    public void NewModelInstance_InheritsScenePreference()
    {
        _overlay.SetModelVisibleByUser(false);
        Object.DestroyImmediate(Get<GameObject>(_overlay, "_contentRoot"));
        Invoke(_overlay, "EnsureSharedContent");
        Applied(DjiRenderPoseSource.Visual);
        AssertVisibility(false);
        _overlay.SetModelVisibleByUser(true); AssertVisibility(true);
    }

    [Test]
    public void HiddenPresentation_DoesNotInterruptWorldPropagationOrVisualCommit()
    {
        const long ms = 1_000_000L;
        var coordinator = Get<DjiWorldLocalizationShadowCoordinator>(_world, "_coordinator");
        var positions = new List<TelemetryAircraftPositionSample>
        {
            new(100 * ms, 47d, 19d, 100d),
            new(200 * ms, 47.00001d, 19d, 100.1d),
            new(300 * ms, 47.00002d, 19d, 100.2d)
        };
        Assert.That(coordinator.TryInitializeFromVisualMeasurement(Pose.identity, 100 * ms, true, out var reason), Is.True, reason);
        Assert.That(coordinator.TryBeginRotationPropagation(Pose.identity, 100 * ms, Quaternion.identity, positions, 300f, out reason), Is.True, reason);
        Assert.That(coordinator.TryEvaluateRotationPropagation(200 * ms, 200 * ms, Quaternion.identity, Pose.identity, positions, 300f, out reason), Is.True, reason);
        Applied(DjiRenderPoseSource.TelemetryPropagated);
        var before = _world.Snapshot;
        _controls.ToggleModelVisibility();
        Assert.That(_world.Snapshot, Is.EqualTo(before));
        Assert.That(coordinator.TryEvaluateRotationPropagation(300 * ms, 200 * ms, Quaternion.identity, Pose.identity, positions, 300f, out reason), Is.True, reason);
        Assert.That(_world.Snapshot.lastPropagationTimestampNs, Is.EqualTo(300 * ms));
        Assert.That(_world.Snapshot.worldFromCamera.position.x, Is.GreaterThan(before.worldFromCamera.position.x));
        Assert.That(_world.Snapshot.worldFromBoard, Is.EqualTo(before.worldFromBoard));
        var visual = new Pose(new Vector3(.25f, -.1f, 1f), Quaternion.Euler(2, 3, 4));
        Assert.That(_world.TryCommitVisualRenderAuthority(visual, 350 * ms, out reason), Is.True, reason);
        Assert.That(_world.Snapshot.phase, Is.EqualTo(WorldLocalizationPhase.VisualLocalized));
        Assert.That(_world.Snapshot.lastVisualSourceTimestampNs, Is.EqualTo(350 * ms));
        Assert.That(_overlay.ModelVisibleByUser, Is.False);
        var committed = _world.Snapshot;
        _controls.ToggleModelVisibility();
        Assert.That(_world.Snapshot, Is.EqualTo(committed));
    }

    [Test]
    public void ActualButtonClick_OnlyChangesPresentationPreference()
    {
        Applied(DjiRenderPoseSource.Visual);
        var button = Get<Button>(_controls, "_visibilityButton");
        button.onClick.Invoke();
        Assert.That(_overlay.ModelVisibleByUser, Is.False); AssertVisibility(false);
        button.onClick.Invoke();
        Assert.That(_overlay.ModelVisibleByUser, Is.True); AssertVisibility(true);
    }

    [Test]
    public void SceneRecreation_StartsShown_NotPersistedInPreferences()
    {
        for (int i = 0; i < 2; i++)
        {
            var scene = EditorSceneManager.NewPreviewScene();
            var root = new GameObject("New DroneView presentation"); SceneManager.MoveGameObjectToScene(root, scene);
            var overlay = root.AddComponent<DjiSharedReferenceCubeOverlay>();
            var controls = root.AddComponent<DjiModelPresentationControls>(); controls.Configure(overlay);
            Assert.That(overlay.ModelVisibleByUser, Is.True);
            overlay.SetModelVisibleByUser(false);
            EditorSceneManager.ClosePreviewScene(scene);
            Assert.That(overlay == null && controls == null, Is.True);
        }
    }

    [TestCase(ARContentType.Cube)]
    public void Toggle_DoesNotHideOtherSelectedContent(ARContentType selection)
    {
        Object.DestroyImmediate(Get<GameObject>(_overlay, "_contentRoot"));
        ARContentSelection.Set(selection); Invoke(_overlay, "EnsureSharedContent"); Applied(DjiRenderPoseSource.Visual);
        _controls.Configure(_overlay); _controls.ToggleModelVisibility();
        Assert.That(Get<Button>(_controls, "_visibilityButton").interactable, Is.False);
        Assert.That(Get<Renderer[]>(_overlay, "_contentRenderers").All(r => r.enabled), Is.True);
    }

    [Test]
    public void Controls_AreSafeAreaAnchoredAndDoNotDuplicateOnReconfigure()
    {
        int count = _controlsObject.GetComponentsInChildren<Transform>(true).Length;
        _controls.Configure(_overlay); _controls.Configure(_overlay);
        Assert.That(_controlsObject.GetComponentsInChildren<Transform>(true).Length, Is.EqualTo(count));
        Invoke(_controls, "ApplySafeArea", new Rect(80, 20, 1800, 1000), new Vector2Int(1920, 1080));
        var safe = Get<RectTransform>(_controls, "_safeArea"); var panel = Get<RectTransform>(_controls, "_panel");
        Assert.That(safe.anchorMin.x, Is.EqualTo(80f / 1920).Within(1e-6));
        Assert.That(panel.anchorMin, Is.EqualTo(Vector2.right));
        Assert.That(panel.anchoredPosition, Is.EqualTo(new Vector2(-24, 24)));
        Assert.That(panel.rect.width, Is.EqualTo(146f));
        Assert.That(_controlsObject.GetComponentsInChildren<Button>().Length, Is.EqualTo(1));
        Assert.That(_controlsObject.GetComponentsInChildren<Text>().All(t => !t.raycastTarget), Is.True);
    }

    [Test]
    public void CapturePlaceholderAndApis_AreAbsent()
    {
        Assert.That(typeof(DjiModelPresentationControls).GetProperty("ScreenshotAvailable"), Is.Null);
        Assert.That(typeof(DjiModelPresentationControls).GetMethod("RequestScreenshot"), Is.Null);
        Assert.That(_controlsObject.GetComponentsInChildren<Button>(true).Select(b => b.name), Is.EqualTo(new[] { "Model Visibility" }));
        Assert.That(_controlsObject.GetComponentsInChildren<Text>().Any(t => t.text.Contains("CAPTURE")), Is.False);
        Assert.That(_controlsObject.GetComponentsInChildren<Text>().Any(t => t.text.Contains("Saved")), Is.False);
        Assert.That(_controlsObject.GetComponentsInChildren<Camera>(), Is.Empty);
        Assert.That(typeof(DjiModelPresentationControls).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Any(f => typeof(Texture).IsAssignableFrom(f.FieldType)), Is.False);
    }

    [Test]
    public void UnconfiguredAndDisabledControls_DoNotMutateTracking()
    {
        var before = _world.Snapshot;
        _controls.Configure(null); _controls.ToggleModelVisibility();
        Assert.That(Get<Button>(_controls, "_visibilityButton").interactable, Is.False);
        _controls.enabled = false; Invoke(_controls, "OnDisable");
        Assert.That(Get<Canvas>(_controls, "_canvas").enabled, Is.False);
        _controls.enabled = true; Invoke(_controls, "OnEnable");
        Assert.That(Get<Canvas>(_controls, "_canvas").enabled, Is.True);
        Assert.That(_world.Snapshot, Is.EqualTo(before));
    }

    [Test]
    public void SingleModelButton_HasNoInvisibleTapBlockers_AndAdaptsToNarrowSafeArea()
    {
        Invoke(_controls, "ApplySafeArea", new Rect(100, 20, 220, 400), new Vector2Int(440, 480));
        var safe = Get<RectTransform>(_controls, "_safeArea");
        var panel = Get<RectTransform>(_controls, "_panel");
        Assert.That(safe.anchorMin.x, Is.EqualTo(100f / 440).Within(1e-6));
        Assert.That(safe.anchorMax.x, Is.EqualTo(320f / 440).Within(1e-6));
        Assert.That(panel.rect.width, Is.EqualTo(146f));
        var button = Get<Button>(_controls, "_visibilityButton");
        Assert.That(_controlsObject.GetComponentsInChildren<Graphic>(true).Where(g => g.raycastTarget).Select(g => g.gameObject),
            Is.EqualTo(new[] { button.gameObject }));
        Assert.That(button.GetComponent<RectTransform>().anchorMin, Is.EqualTo(Vector2.zero));
        Assert.That(button.GetComponent<RectTransform>().anchorMax, Is.EqualTo(Vector2.one));
    }

    private void Applied(DjiRenderPoseSource source, bool valid = true)
    {
        Set(_overlay, "_hasAuthoritativeVisualAcquisition", true);
        Invoke(_overlay, "SetCubeVisible", valid, "PRESENTATION_TEST");
        Set(_overlay, "_lastRenderPoseSource", source);
    }
    private void AssertVisibility(bool expected)
    {
        var renderers = Get<Renderer[]>(_overlay, "_contentRenderers");
        Assert.That(renderers.Select(r => r.name), Is.EquivalentTo(new[] { "wood", "roof", "drainage" }));
        Assert.That(renderers.All(r => r.enabled == expected), Is.True);
    }
    private void FreshTranslation()
    {
        var coordinator = Get<object>(_world, "_coordinator");
        Set(coordinator, "<ProductionHorizontalGnssApplied>k__BackingField", true);
        Set(coordinator, "<ProductionHorizontalGnssState>k__BackingField", HorizontalGnssSmoothingState.FollowingFreshGnss);
        Set(coordinator, "<ProductionHorizontalGnssReason>k__BackingField", string.Empty);
        var e = new VerticalAltitudeSmoothingEstimator(.3f); e.Initialize(100);
        Set(coordinator, "<ProductionVerticalAltitude>k__BackingField", new VerticalAltitudeProductionSnapshot(true, true, 100, 100, 100, 100, 0, 0, 0, 0, Vector3.zero, e.Update(100, true, 0, 100)));
    }
    private void RefreshStatus() => Invoke(_status, "RefreshStatus");
    private static T Get<T>(object o, string n) => (T)o.GetType().GetField(n, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(o);
    private static void Set(object o, string n, object v) => o.GetType().GetField(n, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(o, v);
    private static void Invoke(object o, string n, params object[] a) => o.GetType().GetMethod(n, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(o, a);
}
