using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

public sealed class FourTagMultiStartIntegrationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const long Ms = 1_000_000;
    [Serializable] private sealed class Fixture
    {
        public DjiBoardVisionProvider.NativeBoardResult result;
        public float[] expectedQuaternion;
        public int selectedInitializerId;
        public bool nativeCompiledExperiment;
    }
    private GameObject _root, _overlayRoot, _cameraRoot, _content;
    private DjiBoardVisionProvider _board;
    private DjiSharedReferenceCubeOverlay _overlay;
    private Camera _camera;
    private Fixture _fixture;
    private Pose _selected;
    private Type MetricsType => typeof(DjiBoardVisionProvider).GetNestedType("VisualPoseMetrics", BindingFlags.NonPublic);

    [SetUp]
    public void SetUp()
    {
        var directory = Environment.GetEnvironmentVariable("DJI_MULTISTART_FIXTURE_DIR");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set DJI_MULTISTART_FIXTURE_DIR to run with the native replay fixture.");
        _fixture = JsonUtility.FromJson<Fixture>(File.ReadAllText(Path.Combine(directory, "selected-native-unity-fixture.json")));
        _root = new GameObject("Multi-start downstream integration"); _root.SetActive(false);
        _board = _root.AddComponent<DjiBoardVisionProvider>();
        Set("<CurrentRenderPoseSource>k__BackingField", DjiRenderPoseSource.TelemetryPropagated);
        var args = new object[] { _fixture.result, Pose.identity };
        Assert.That((bool)Call("TryGetCameraFromBoard", args), Is.True);
        _selected = (Pose)args[1];
        SeedShortWindowPrediction();
        _overlayRoot = new GameObject("Multi-start overlay"); _overlayRoot.SetActive(false);
        _overlay = _overlayRoot.AddComponent<DjiSharedReferenceCubeOverlay>();
        _cameraRoot = new GameObject("Multi-start camera", typeof(Camera)); _camera = _cameraRoot.GetComponent<Camera>();
        _content = new GameObject("Multi-start content");
        typeof(DjiSharedReferenceCubeOverlay).GetField("_boardVisionProvider", Private).SetValue(_overlay, _board);
        typeof(DjiSharedReferenceCubeOverlay).GetField("_overlayCamera", Private).SetValue(_overlay, _camera);
        typeof(DjiSharedReferenceCubeOverlay).GetField("_contentRoot", Private).SetValue(_overlay, _content);
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(_overlayRoot); Object.DestroyImmediate(_content);
        Object.DestroyImmediate(_cameraRoot); Object.DestroyImmediate(_root);
    }

    [Test]
    public void ExecutedNativeResult_RoundTripsUnchangedThroughOpticalMatrixConversion()
    {
        Assert.That(_fixture.nativeCompiledExperiment, Is.True);
        Assert.That(_fixture.selectedInitializerId, Is.EqualTo(3));
        Assert.That(_fixture.result.visionFrameTimestampNs, Is.EqualTo(125745904631969L));
        Assert.That(_fixture.result.reprojectionRms, Is.EqualTo(.4147568f).Within(.0003f));
        var q = _fixture.expectedQuaternion;
        Assert.That(Quaternion.Angle(_selected.rotation, new Quaternion(q[0], q[1], q[2], q[3])), Is.LessThan(.05f));
        Assert.That(_selected.position, Is.EqualTo(new Vector3(_fixture.result.cameraFromBoardPosition[0],
            _fixture.result.cameraFromBoardPosition[1], _fixture.result.cameraFromBoardPosition[2])));
    }

    [TestCase(false, false, false)] [TestCase(false, false, true)]
    [TestCase(false, true, false)] [TestCase(false, true, true)]
    [TestCase(true, false, false)] [TestCase(true, false, true)]
    [TestCase(true, true, false)] [TestCase(true, true, true)]
    public void SelectedPose_StillRequiresConfirmationDwellAndActualCameraApplication(bool border, bool stale, bool retention)
    {
        // This offline policy matrix intentionally isolates the legacy handover:
        // synthetic Observe calls have no arrival clock or validated source queue.
        Set("_validatedTrackingSafetyEnabled", false);
        for (var t = 1000; t <= 1500; t += 100)
        {
            var metrics = Metrics(t);
            var evaluation = Evaluate(metrics, border, stale, retention);
            Apply(metrics, evaluation);
            if (_board.CurrentVisualPoseQuality == DjiBoardVisionProvider.VisualPoseQuality.HighConfidence)
                Call("ObserveVisualReacquisitionCandidate", NativeResult(t), metrics, false);
            if (t < 1500) Assert.That(_board.HasHighConfidenceCameraFromBoard, Is.False, "No native promotion shortcut.");
        }
        Assert.That(_board.HasHighConfidenceCameraFromBoard, Is.True);
        Assert.That(Get("_currentHighConfidenceVisualTimestampNs"), Is.EqualTo(1500 * Ms));
        Assert.That(_board.TryGetRenderCameraFromBoard(2, out var applied, out var source, out _), Is.True);
        Assert.That(source, Is.EqualTo(DjiRenderPoseSource.Visual));
        Assert.That(Quaternion.Angle(applied.rotation, _selected.rotation), Is.LessThan(.05f));
        typeof(DjiSharedReferenceCubeOverlay).GetMethod("ApplyStandaloneBoardOverlayPose", Private)
            .Invoke(_overlay, new object[] { applied });
        var expected = DjiSharedReferenceCubeOverlay.NativeOpticalToUnityViewBasis * ReferenceFrameTransforms.MatrixFromPose(_selected);
        for (var row = 0; row < 4; row++) for (var column = 0; column < 4; column++)
            Assert.That(_camera.worldToCameraMatrix[row, column], Is.EqualTo(expected[row, column]).Within(.0001f));
    }

    [Test]
    public void SelectedNativeTransport_TraversesRealProcessResultWithoutAdmissionShortcut()
    {
        Assert.That(Get("_validatedTrackingSafetyEnabled"), Is.True, "Real transport uses the normal validated policy.");
        Set("<HasHighConfidenceCameraFromBoard>k__BackingField", true);
        Set("<CurrentHighConfidenceCameraFromBoard>k__BackingField", _selected);
        Set("_currentHighConfidenceVisualTimestampNs", 500 * Ms);
        for (var t = 1000; t <= 1500; t += 100)
        {
            var result = JsonUtility.FromJson<DjiBoardVisionProvider.NativeBoardResult>(JsonUtility.ToJson(_fixture.result));
            result.timestampNs = result.visionFrameTimestampNs = t * Ms;
            result.visionFrameId = result.frameSequence = t;
            Call("ProcessResult", result, t * Ms + 10 * Ms);
            if (t < 1500) Assert.That(Get("_currentHighConfidenceVisualTimestampNs"), Is.EqualTo(500 * Ms));
        }
        Assert.That(_board.HasHighConfidenceCameraFromBoard, Is.True);
        Assert.That(Get("_currentHighConfidenceVisualTimestampNs"), Is.EqualTo(1500 * Ms));
        Assert.That(_board.TryGetRenderCameraFromBoard(2, out var pose, out var source, out _), Is.True);
        Assert.That(source, Is.EqualTo(DjiRenderPoseSource.Visual));
        Assert.That(Quaternion.Angle(pose.rotation, _selected.rotation), Is.LessThan(.05f));
        Assert.That(Vector3.Distance(pose.position, _selected.position), Is.LessThan(.0001f));
    }

    [TestCase(false, false, false)] [TestCase(true, true, true)]
    public void NativeSelection_DoesNotBypassRotationOrTranslationGate(bool border, bool stale, bool retention)
    {
        Set("<CurrentRenderPoseSource>k__BackingField", DjiRenderPoseSource.Visual);
        var rotation = Evaluate(Metrics(1100, rotation: 11), border, stale, retention);
        Assert.That(Reason(rotation), Is.EqualTo("POSE_ROTATION_JUMP"));
        var translation = Evaluate(Metrics(1200, jump: .2f), border, stale, retention);
        Assert.That(Reason(translation), Is.EqualTo("POSE_TRANSLATION_JUMP_SEVERE"));
        Assert.That(_board.HasHighConfidenceCameraFromBoard, Is.False);
    }

    [Test]
    public void SelectedVisualPose_PropagatesFromExactAcceptedBaselineWithoutFrameChange()
    {
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        Assert.That(coordinator.TryInitializeFromVisualMeasurement(_selected, 1500 * Ms, true, out var reason), Is.True, reason);
        Assert.That(coordinator.TryBeginRotationPropagation(_selected, 1500 * Ms, Quaternion.identity, out reason), Is.True, reason);
        var nav = Quaternion.Euler(7, -12, 3);
        var prediction = ReferenceFrameTransforms.PredictCameraFromBoardRotationOnly(_selected, Quaternion.identity, nav);
        Assert.That(coordinator.TryEvaluateRotationPropagation(1600 * Ms, 1600 * Ms, nav, prediction, null, 300, out reason), Is.True, reason);
        var actual = ReferenceFrameTransforms.Invert(coordinator.Snapshot.worldFromCamera);
        Assert.That(Quaternion.Angle(actual.rotation, prediction.rotation), Is.LessThan(.05f));
        Assert.That(coordinator.LastProductionBaselineTimestampNs, Is.EqualTo(1500 * Ms));
        Assert.That(coordinator.Snapshot.worldFromBoard.position, Is.EqualTo(Vector3.zero));
        Assert.That(coordinator.Snapshot.worldFromBoard.rotation, Is.EqualTo(Quaternion.identity));
    }

    [Test]
    public void NormalEditorFlagsAndExistingThresholds_AreNotChangedByNativeExperiment()
    {
        // Keep the historical ID while checking the consolidated normal defaults.
        Assert.That(DjiAprilTagBorderExperiment.Enabled, Is.True);
        Assert.That(DjiAprilTagStaleJumpExperiment.Enabled, Is.True);
        Assert.That(DjiAprilTagBorderRetentionExperiment.Enabled, Is.True);
        Assert.That(DjiAprilTagNoMarkersQualityLossExperiment.Enabled, Is.True);
        Assert.That(DjiValidatedTrackingCandidate.Enabled, Is.True);
        Assert.That(Get("_validatedTrackingSafetyEnabled"), Is.True);
        Assert.That(Get("highConfidenceMinimumBorderDistancePixels"), Is.EqualTo(64f));
        Assert.That(Get("highConfidenceMaximumFrameRotationJumpDegrees"), Is.EqualTo(10f));
        Assert.That(Get("reacquisitionMaximumRotationDifferenceDegrees"), Is.EqualTo(6f));
        Assert.That(Get("reacquisitionStableTimeSeconds"), Is.EqualTo(.25f));
    }

    private void SeedShortWindowPrediction()
    {
        Set("_shadowPropagationActive", true);
        Set("_hasLatestShadowPrediction", true);
        Set("_hasShadowRotationPropagationBaseline", true);
        var baselineType = typeof(DjiBoardVisionProvider).GetNestedType("ShadowRotationPropagationBaseline", BindingFlags.NonPublic);
        Set("_shadowRotationPropagationBaseline", Activator.CreateInstance(baselineType, new object[] {
            500 * Ms, _selected, Quaternion.identity, false, default(TelemetryAircraftPositionSample) }));

        var telemetry = _root.AddComponent<DjiCameraPoseProvider>();
        typeof(DjiCameraPoseProvider).GetField("<HasTelemetryOrientation>k__BackingField", Private).SetValue(telemetry, true);
        var history = (List<DjiCameraPoseProvider.TelemetryOrientationSample>)typeof(DjiCameraPoseProvider)
            .GetField("_telemetryOrientationHistory", Private).GetValue(telemetry);
        for (var timestampMs = 1000; timestampMs <= 1500; timestampMs += 100)
            history.Add(new DjiCameraPoseProvider.TelemetryOrientationSample(timestampMs * Ms, Quaternion.identity, true, 0, 0));
        Set("_telemetryProvider", telemetry);
    }

    private object Metrics(int timestamp, float rotation = 0, float jump = .001f) =>
        Activator.CreateInstance(MetricsType, new object[] { timestamp * Ms, 4, 16, "0,1,2,3",
            _fixture.result.reprojectionRms, _fixture.result.maxResidual, 30f,
            _fixture.result.reprojectionRms / 30f, _fixture.result.maxResidual / 30f, 90f, 10000f,
            _selected, jump, rotation, .1f, false, float.NaN, false, float.NaN });
    private object Evaluate(object metrics, bool border, bool stale, bool retention) =>
        Call("EvaluateVisualPoseQualityForAllExperiments", metrics, border, stale, retention);
    private void Apply(object metrics, object evaluation) => typeof(DjiBoardVisionProvider)
        .GetMethod("UpdateVisualPoseQuality", Private, null, new[] { MetricsType, evaluation.GetType() }, null)
        .Invoke(_board, new[] { metrics, evaluation });
    private static string Reason(object evaluation) => (string)evaluation.GetType().GetField("reason").GetValue(evaluation);
    private DjiBoardVisionProvider.NativeBoardResult NativeResult(int timestamp) => new() {
        timestampNs = timestamp * Ms, visionFrameTimestampNs = timestamp * Ms, visionFrameId = timestamp,
        markerCount = 4, reprojectionRms = _fixture.result.reprojectionRms };
    private object Call(string method, params object[] args) => typeof(DjiBoardVisionProvider).GetMethod(method, Private).Invoke(_board, args);
    private object Get(string field) => typeof(DjiBoardVisionProvider).GetField(field, Private).GetValue(_board);
    private void Set(string field, object value) => typeof(DjiBoardVisionProvider).GetField(field, Private).SetValue(_board, value);
}
