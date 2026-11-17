using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

public sealed class DroneViewTrackingStatusTests
{
    private GameObject _providerObject, _videoObject, _overlayObject, _hudObject, _worldObject;
    private DjiBoardVisionProvider _provider;
    private DjiSharedReferenceCubeOverlay _overlay;
    private DjiTrackingStatusIndicator _hud;
    private DjiWorldLocalizationShadow _world;
    private ARContentType _selection;

    [SetUp]
    public void SetUp()
    {
        _selection = ARContentSelection.Selected;
        ARContentSelection.Set(ARContentType.RoofCanopy);
        _providerObject = new GameObject("Status test provider"); _provider = _providerObject.AddComponent<DjiBoardVisionProvider>();
        _videoObject = new GameObject("Status test video", typeof(Camera));
        _overlayObject = new GameObject("Status test overlay"); _overlay = _overlayObject.AddComponent<DjiSharedReferenceCubeOverlay>();
        _overlay.Configure(_videoObject.GetComponent<Camera>(), _provider);
        _worldObject = new GameObject("Status test world"); _world = _worldObject.AddComponent<DjiWorldLocalizationShadow>();
        _hudObject = new GameObject("Status test HUD"); _hud = _hudObject.AddComponent<DjiTrackingStatusIndicator>();
        _hud.Configure(_overlay, _world);
    }

    [TearDown]
    public void TearDown()
    {
        var content = Get<GameObject>(_overlay, "_contentRoot"); var camera = Get<Camera>(_overlay, "_overlayCamera");
        Object.DestroyImmediate(_hudObject); Object.DestroyImmediate(_overlayObject);
        if (content != null) Object.DestroyImmediate(content);
        if (camera != null) Object.DestroyImmediate(camera.gameObject);
        Object.DestroyImmediate(_worldObject); Object.DestroyImmediate(_providerObject); Object.DestroyImmediate(_videoObject);
        ARContentSelection.Set(_selection);
    }

    [TestCase(DjiRenderPoseSource.None), TestCase(DjiRenderPoseSource.Visual),
     TestCase(DjiRenderPoseSource.TelemetryPropagated)]
    public void NoAuthoritativeReference_AlwaysWaiting(DjiRenderPoseSource source)
    {
        Assert.That(DjiTrackingStatusIndicator.Resolve(source, false, false), Is.EqualTo(DjiTrackingStatus.Waiting));
        Assert.That(DjiTrackingStatusIndicator.Resolve(source, false, true), Is.EqualTo(DjiTrackingStatus.Waiting));
    }

    [TestCase(DjiRenderPoseSource.TelemetryPropagated)]
    public void PropagatedAuthority_IsTelemetryWithExplicitLimitedQualifier(DjiRenderPoseSource source)
    {
        Assert.That(DjiTrackingStatusIndicator.Resolve(source, true, false), Is.EqualTo(DjiTrackingStatus.Telemetry));
        Assert.That(DjiTrackingStatusIndicator.Resolve(source, true, true), Is.EqualTo(DjiTrackingStatus.TelemetryLimited));
    }

    [Test]
    public void AcceptedReferenceWithoutDisplayedPose_IsLost_NotWaitingOrVisual()
    {
        Applied(DjiRenderPoseSource.Visual, true, false);
        Refresh();
        Assert.That(_overlay.DisplayedRenderPoseSource, Is.EqualTo(DjiRenderPoseSource.None));
        Assert.That(_hud.DisplayedStatus, Is.EqualTo(DjiTrackingStatus.Lost));
    }

    [Test]
    public void StatusTransitionsFollowAppliedAuthority_NotCandidateOrSatelliteCount()
    {
        Assert.That(_hud.DisplayedStatus, Is.EqualTo(DjiTrackingStatus.Waiting));
        Set(_provider, "<CurrentRenderPoseSource>k__BackingField", DjiRenderPoseSource.Visual);
        Refresh(); Assert.That(_hud.DisplayedStatus, Is.EqualTo(DjiTrackingStatus.Waiting));
        Applied(DjiRenderPoseSource.Visual, true, true); Refresh();
        Assert.That(_hud.DisplayedStatus, Is.EqualTo(DjiTrackingStatus.Visual));
        Applied(DjiRenderPoseSource.TelemetryPropagated, true, true); Refresh();
        Assert.That(_hud.DisplayedStatus, Is.EqualTo(DjiTrackingStatus.TelemetryLimited));
        Applied(DjiRenderPoseSource.None, true, false); Refresh();
        Assert.That(_hud.DisplayedStatus, Is.EqualTo(DjiTrackingStatus.Lost));
        Applied(DjiRenderPoseSource.Visual, true, true); Refresh();
        Assert.That(_hud.DisplayedStatus, Is.EqualTo(DjiTrackingStatus.Visual));
    }

    [TestCase(HorizontalGnssSmoothingState.Uninitialized),
     TestCase(HorizontalGnssSmoothingState.DegradedGnssQuality), TestCase(HorizontalGnssSmoothingState.DegradedStaleGnss)]
    public void HeldOrUntrustedHorizontal_NeverClaimsUnqualifiedTelemetry(HorizontalGnssSmoothingState state)
    {
        FreshTranslation(); SetCoordinator("ProductionHorizontalGnssState", state);
        Applied(DjiRenderPoseSource.TelemetryPropagated, true, true); Refresh();
        Assert.That(_hud.DisplayedStatus, Is.EqualTo(DjiTrackingStatus.TelemetryLimited));
        Assert.That(Get<Text>(_hud, "_label").text, Does.Contain("LIMITED"));
    }

    [TestCase("PENDING_TEMPORAL_CONSISTENCY"), TestCase("WITHIN_STATIONARY_NOISE_ENVELOPE")]
    public void RejectedTarget_RemainsQualifiedWithoutChangingAdmission(string reason)
    {
        FreshTranslation(); SetCoordinator("ProductionHorizontalGnssReason", reason);
        Applied(DjiRenderPoseSource.TelemetryPropagated, true, true); Refresh();
        Assert.That(_hud.DisplayedStatus, Is.EqualTo(DjiTrackingStatus.TelemetryLimited));
        Assert.That(_world.ProductionHorizontalGnssReason, Is.EqualTo(reason));
    }

    [TestCase(""), TestCase("NO_NEW_GNSS_TARGET")]
    public void FreshExistingTranslation_CanUseTelemetryLabel_WithoutAccuracyClaim(string reason)
    {
        FreshTranslation(); SetCoordinator("ProductionHorizontalGnssReason", reason);
        Applied(DjiRenderPoseSource.TelemetryPropagated, true, true); Refresh();
        Assert.That(_hud.DisplayedStatus, Is.EqualTo(DjiTrackingStatus.Telemetry));
        Assert.That(Get<Text>(_hud, "_label").text, Is.EqualTo("TELEMETRY"));
    }

    [Test]
    public void PendingTarget_RemainsLimitedAcrossRepeatedPolling()
    {
        FreshTranslation();
        var estimator = new HorizontalGnssSmoothingEstimator(); estimator.Initialize(100);
        estimator.Update(100, true, Vector2.zero, 100);
        estimator.Update(200, true, Vector2.one, 200);
        var repeated = estimator.Update(201, true, Vector2.one, 200);
        Assert.That(repeated.failureReason, Is.EqualTo("NO_NEW_GNSS_TARGET"));
        Assert.That(repeated.rawGnssTargetTimestampNs, Is.GreaterThan(repeated.acceptedGnssTargetTimestampNs));
        object snapshot = _world.HorizontalGnssSmoothingShadow;
        snapshot.GetType().GetField("estimate").SetValue(snapshot, repeated);
        SetCoordinator("LatestHorizontalGnssSmoothingShadow", snapshot);
        SetCoordinator("ProductionHorizontalGnssReason", repeated.failureReason);
        Applied(DjiRenderPoseSource.TelemetryPropagated, true, true); Refresh();
        Assert.That(_hud.DisplayedStatus, Is.EqualTo(DjiTrackingStatus.TelemetryLimited));
        Refresh(); Assert.That(_hud.DisplayedStatus, Is.EqualTo(DjiTrackingStatus.TelemetryLimited));
    }

    [Test]
    public void DisabledOverlayOrCamera_DoesNotLeaveFalseVisualStatus()
    {
        Applied(DjiRenderPoseSource.Visual, true, true); Refresh();
        _overlay.enabled = false; Refresh(); Assert.That(_hud.DisplayedStatus, Is.EqualTo(DjiTrackingStatus.Lost));
        _overlay.enabled = true; _videoObject.GetComponent<Camera>().enabled = false; Refresh();
        Assert.That(_hud.DisplayedStatus, Is.EqualTo(DjiTrackingStatus.Lost));
        _videoObject.GetComponent<Camera>().enabled = true; Refresh();
        Assert.That(_hud.DisplayedStatus, Is.EqualTo(DjiTrackingStatus.Visual));
    }

    [Test]
    public void ReconfigureForNewSession_ReturnsToWaiting_WithoutDuplicatingUi()
    {
        Applied(DjiRenderPoseSource.Visual, true, true); Refresh();
        int count = _hudObject.GetComponentsInChildren<Transform>(true).Length;
        Set(_overlay, "_hasAuthoritativeVisualAcquisition", false); Set(_overlay, "_cubeVisible", false);
        _hud.Configure(_overlay, _world);
        Assert.That(_hud.DisplayedStatus, Is.EqualTo(DjiTrackingStatus.Waiting));
        Assert.That(_hudObject.GetComponentsInChildren<Transform>(true).Length, Is.EqualTo(count));
    }

    [Test]
    public void EnableDisableAndRepeatedRefresh_PreserveOneUiAndAllocateNoStableStateText()
    {
        Applied(DjiRenderPoseSource.Visual, true, true); Refresh();
        var canvas = Get<Canvas>(_hud, "_canvas");
        _hud.enabled = false; Invoke(_hud, "OnDisable"); Assert.That(canvas.enabled, Is.False);
        _hud.enabled = true; Invoke(_hud, "OnEnable"); Assert.That(canvas.enabled, Is.True);
        var action = (Action)Delegate.CreateDelegate(typeof(Action), _hud, typeof(DjiTrackingStatusIndicator).GetMethod("RefreshStatus", BindingFlags.Instance | BindingFlags.NonPublic));
        int count = _hudObject.GetComponentsInChildren<Transform>(true).Length;
        string text = Get<Text>(_hud, "_label").text;
        for (int i = 0; i < 100; i++) action();
        long controlStart = GC.GetAllocatedBytesForCurrentThread(); var control = new byte[8192]; GC.KeepAlive(control);
        bool available = GC.GetAllocatedBytesForCurrentThread() > controlStart;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) action();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        if (available) Assert.That(allocated, Is.Zero);
        Assert.That(Get<Text>(_hud, "_label").text, Is.SameAs(text));
        Assert.That(_hudObject.GetComponentsInChildren<Transform>(true).Length, Is.EqualTo(count));
    }

    [Test]
    public void SafeAreaAndLabelBacking_StayBelowHomeAndDoNotInterceptTouches()
    {
        typeof(DjiTrackingStatusIndicator).GetMethod("ApplySafeArea", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(_hud, new object[] { new Rect(80, 20, 1800, 1000), new Vector2Int(1920, 1080) });
        var safe = Get<RectTransform>(_hud, "_safeArea"); var panel = Get<RectTransform>(_hud, "_panel");
        Assert.That(safe.anchorMin.x, Is.EqualTo(80f / 1920).Within(1e-6));
        Assert.That(safe.anchorMax.y, Is.EqualTo(1020f / 1080).Within(1e-6));
        Assert.That(panel.anchoredPosition.y, Is.LessThan(-82));
        Assert.That(panel.rect.width, Is.LessThanOrEqualTo(252));
        Assert.That(panel.GetComponent<Image>().color.a, Is.GreaterThan(.8f));
        Assert.That(_hudObject.GetComponentsInChildren<Graphic>().All(g => !g.raycastTarget), Is.True);
        Assert.That(_hudObject.GetComponentsInChildren<GraphicRaycaster>(), Is.Empty);
    }

    [Test]
    public void LoggerStartup_NoLongerCreatesVisibleDeveloperButtons_LoggingApiRemains()
    {
        var go = new GameObject("Marker startup test");
        try
        {
            var logger = go.AddComponent<DjiTelemetryValidationLogger>();
            Invoke(logger, "StartMotionDiagnostics");
            Assert.That(go.GetComponentsInChildren<Button>(true), Is.Empty);
            Assert.That(go.GetComponent(typeof(DjiTelemetryValidationLogger).Assembly.GetType("DjiMotionMarkerHud")), Is.Null);
            Assert.That(typeof(DjiTelemetryValidationLogger).GetMethod("RecordUserMotionMarker"), Is.Not.Null);
            Invoke(logger, "StopMotionDiagnostics");
        }
        finally { Object.DestroyImmediate(go); }
    }

    [Test]
    public void ReadOnlyUi_DoesNotMutatePoseOrExistingSatBatteryIndicators()
    {
        var snapshot = _world.Snapshot;
        var telemetryObject = new GameObject("Status telemetry test");
        var satelliteObject = new GameObject("Status SAT test"); var batteryObject = new GameObject("Status BAT test");
        try
        {
            var telemetry = telemetryObject.AddComponent<DjiCameraPoseProvider>();
            var assembly = typeof(DjiCameraPoseProvider).Assembly;
            var sat = satelliteObject.AddComponent(assembly.GetType("DjiSatelliteIndicator"));
            var bat = batteryObject.AddComponent(assembly.GetType("DjiAircraftBatteryIndicator"));
            Invoke(sat, "Awake"); Invoke(bat, "Awake");
            Set(sat, "_telemetryProvider", telemetry); Set(bat, "_telemetryProvider", telemetry);
            Set(telemetry, "<LatestGpsSatelliteCount>k__BackingField", (int?)26);
            Set(telemetry, "<LatestAircraftBatteryPercent>k__BackingField", (int?)78);
            Invoke(sat, "Update"); Invoke(bat, "Update"); Refresh();
            Assert.That(Get<Text>(sat, "_label").text, Is.EqualTo("SAT 26"));
            Assert.That(Get<Text>(bat, "_label").text, Is.EqualTo("BAT 78%"));
            Set(telemetry, "<LatestAircraftBatteryPercent>k__BackingField", (int?)0); Set(bat, "_nextRefreshTime", 0f); Invoke(bat, "Update");
            Assert.That(Get<Text>(bat, "_label").text, Is.EqualTo("BAT 0%"));
            Set(telemetry, "<LatestAircraftBatteryPercent>k__BackingField", null); Set(bat, "_nextRefreshTime", 0f); Invoke(bat, "Update");
            Assert.That(Get<Text>(bat, "_label").text, Is.EqualTo("BAT --"));
            Assert.That(_world.Snapshot, Is.EqualTo(snapshot));
        }
        finally { Object.DestroyImmediate(batteryObject); Object.DestroyImmediate(satelliteObject); Object.DestroyImmediate(telemetryObject); }
    }

    [Test]
    public void SceneUnloadAndReopen_StartWithOneWaitingHud()
    {
        for (int i = 0; i < 2; i++)
        {
            var scene = EditorSceneManager.NewPreviewScene();
            var root = new GameObject("Reopened DroneView HUD"); SceneManager.MoveGameObjectToScene(root, scene);
            var hud = root.AddComponent<DjiTrackingStatusIndicator>();
            hud.Configure(null, null);
            Assert.That(hud.DisplayedStatus, Is.EqualTo(DjiTrackingStatus.Waiting));
            Assert.That(root.GetComponentsInChildren<Canvas>().Length, Is.EqualTo(1));
            EditorSceneManager.ClosePreviewScene(scene);
            Assert.That(hud == null, Is.True);
        }
    }

    private void Applied(DjiRenderPoseSource source, bool acquired, bool visible)
    {
        Set(_overlay, "_hasAuthoritativeVisualAcquisition", acquired);
        Invoke(_overlay, "SetCubeVisible", visible, "UI_TEST");
        Set(_overlay, "_lastRenderPoseSource", source);
    }
    private void FreshTranslation()
    {
        SetCoordinator("ProductionHorizontalGnssApplied", true);
        SetCoordinator("ProductionHorizontalGnssState", HorizontalGnssSmoothingState.FollowingFreshGnss);
        SetCoordinator("ProductionHorizontalGnssReason", string.Empty);
        var e = new VerticalAltitudeSmoothingEstimator(.3f); e.Initialize(100);
        SetCoordinator("ProductionVerticalAltitude", new VerticalAltitudeProductionSnapshot(true, true, 100, 100, 100, 100, 0, 0, 0, 0, Vector3.zero, e.Update(100, true, 0, 100)));
    }
    private void SetCoordinator(string property, object value) => Set(Get<object>(_world, "_coordinator"), "<" + property + ">k__BackingField", value);
    private void Refresh() => Invoke(_hud, "RefreshStatus");
    private static T Get<T>(object o, string name) => (T)o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(o);
    private static void Set(object o, string name, object value) => o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(o, value);
    private static void Invoke(object o, string name, params object[] args) => o.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(o, args);
}
