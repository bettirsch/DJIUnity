using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

public sealed class AprilTagBorderRetentionExperimentTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const long Ms = 1_000_000L;
    private GameObject _object, _overlayObject, _cameraObject, _content, _loggerObject;
    private DjiBoardVisionProvider _board;
    private DjiSharedReferenceCubeOverlay _overlay;
    private Camera _camera;
    private StringWriter _output;
    private Type MetricsType => typeof(DjiBoardVisionProvider).GetNestedType("VisualPoseMetrics", BindingFlags.NonPublic);

    [SetUp]
    public void SetUp()
    {
        _object = new GameObject("Border retention provider"); _object.SetActive(false);
        _board = _object.AddComponent<DjiBoardVisionProvider>();
        SetSource(DjiRenderPoseSource.Visual); SetQuality(DjiBoardVisionProvider.VisualPoseQuality.HighConfidence);
        Set("_hasLastVisualQualityPose", true);
        Set("<HasHighConfidenceCameraFromBoard>k__BackingField", true);
        Set("<CurrentHighConfidenceCameraFromBoard>k__BackingField", new Pose(new Vector3(0, 0, 2), Quaternion.identity));
        Set("_currentHighConfidenceVisualTimestampNs", 1000 * Ms);
        Set("_lastHighConfidenceCameraFromBoardRealtime", Time.realtimeSinceStartup);
        _overlayObject = new GameObject("Retention overlay"); _overlayObject.SetActive(false);
        _overlay = _overlayObject.AddComponent<DjiSharedReferenceCubeOverlay>();
        _cameraObject = new GameObject("Retention camera", typeof(Camera)); _camera = _cameraObject.GetComponent<Camera>();
        _content = new GameObject("Retention board content");
        SetOverlay("_boardVisionProvider", _board); SetOverlay("_overlayCamera", _camera); SetOverlay("_contentRoot", _content);
        _loggerObject = new GameObject("Retention in-memory logger");
        var logger = _loggerObject.AddComponent<DjiTelemetryValidationLogger>();
        _output = new StringWriter();
        typeof(DjiTelemetryValidationLogger).GetField("_visualLatencyWriter", Private).SetValue(logger, _output);
        typeof(DjiTelemetryValidationLogger).GetField("_telemetrySessionId", Private).SetValue(logger, "retention-test");
        Set("_telemetryValidationLogger", logger);
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(_overlayObject); Object.DestroyImmediate(_content);
        Object.DestroyImmediate(_cameraObject); Object.DestroyImmediate(_object); Object.DestroyImmediate(_loggerObject);
        _output.Dispose();
    }

    [Test]
    public void NormalBuild_IsDisabledAndPreserves64PixelPolicy()
    {
        Assert.That(DjiAprilTagBorderRetentionExperiment.Enabled, Is.False);
        Assert.That(Reason(Evaluate(Metrics(), retention: false)), Is.EqualTo("BORDER_PROXIMITY"));
        Assert.That(Get("highConfidenceMinimumBorderDistancePixels"), Is.EqualTo(64f));
    }

    [TestCase(48f)]
    [TestCase(52f)]
    [TestCase(63.99f)]
    [TestCase(64f)]
    public void ActiveHighConfidenceFourTags_AreBorderEligible(float clearance)
    {
        Assert.That(Reason(Evaluate(Metrics(border: clearance))), Is.Empty);
        Assert.That(Get("_retentionEffectiveThresholdPixels"), Is.EqualTo(48f));
    }

    [Test]
    public void RepeatedValid52PixelPoses_ArePromotedResolvedAndActuallyApplied_NotHeld()
    {
        var previousMatrix = _camera.worldToCameraMatrix;
        for (var i = 1; i <= 5; i++)
        {
            Step(1000 + i * 100, x: i * .01f);
            Assert.That(Get("_currentHighConfidenceVisualTimestampNs"), Is.EqualTo((1000 + i * 100) * Ms));
            Assert.That(Get("_holdingVisualAuthorityForBorderlineQuality"), Is.False);
            var pose = ResolveAndApply();
            Assert.That(pose.position.x, Is.EqualTo(i * .01f).Within(1e-6f));
            Assert.That(_camera.worldToCameraMatrix, Is.Not.EqualTo(previousMatrix));
            var expected = DjiSharedReferenceCubeOverlay.NativeOpticalToUnityViewBasis * ReferenceFrameTransforms.MatrixFromPose(pose);
            for (var row = 0; row < 4; row++) for (var col = 0; col < 4; col++)
                Assert.That(_camera.worldToCameraMatrix[row, col], Is.EqualTo(expected[row, col]).Within(1e-6f));
            previousMatrix = _camera.worldToCameraMatrix;
        }
        Assert.That(AppliedRecords(), Is.EqualTo(5));
        ResolveAndApply();
        Assert.That(AppliedRecords(), Is.EqualTo(5), "Repeated rendering of the same accepted source is not another new pose.");
    }

    [Test]
    public void Below48Pixels_RemainsSevereBorderFailure()
    {
        var m = Metrics(border: 47); var e = Evaluate(m); Apply(m, e);
        Assert.That(Reason(e), Is.EqualTo("BORDER_PROXIMITY"));
        Assert.That(_board.CurrentVisualPoseQuality, Is.EqualTo(DjiBoardVisionProvider.VisualPoseQuality.Degraded));
    }

    [TestCase(DjiRenderPoseSource.None)]
    [TestCase(DjiRenderPoseSource.TelemetryPropagated)]
    public void NoActualVisualAuthority_RetentionCannotBootstrapAcquisition(DjiRenderPoseSource source)
    {
        SetSource(source);
        Assert.That(Reason(Evaluate(Metrics(), border: false)), Is.EqualTo("BORDER_PROXIMITY"));
        Assert.That(Get("_retentionEligibleForEvaluation"), Is.False);
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public void LowerTagCounts_Retain64Pixels(int count)
    {
        Assert.That(Reason(Evaluate(Metrics(count: count))), Is.EqualTo("BORDER_PROXIMITY"));
        Assert.That(Get("_retentionEffectiveThresholdPixels"), Is.EqualTo(64f));
    }

    [Test]
    public void PreviousDegradedState_DoesNotUseRetentionEvenIfSourceStillSaysVisual()
    {
        SetQuality(DjiBoardVisionProvider.VisualPoseQuality.Degraded);
        Assert.That(Reason(Evaluate(Metrics())), Is.EqualTo("BORDER_PROXIMITY"));
    }

    [TestCase(4f, 2f, 0f, "NORMALIZED_RMS")]
    [TestCase(1f, 6f, 0f, "NORMALIZED_CORNER_RESIDUAL")]
    [TestCase(1f, 2f, 11f, "POSE_ROTATION_JUMP")]
    public void IndependentQualityGates_AreStillEvaluated(float rms, float corner, float rotation, string reason)
    {
        Assert.That(Reason(Evaluate(Metrics(rms: rms, corner: corner, rotation: rotation))), Is.EqualTo(reason));
    }

    [Test]
    public void EmergencyJump_IsNotHiddenByFormerBorderlineEarlyReturn()
    {
        Assert.That(Reason(Evaluate(Metrics(jump: .2f))), Is.EqualTo("POSE_TRANSLATION_JUMP_SEVERE"));
    }

    [Test]
    public void MotionSuspects_StillHoldThenDegradeWithoutPromotionShortcut()
    {
        Step(1100, jump: .06f); Step(1200, jump: .06f); Step(1300, jump: .06f);
        Assert.That(Get("_currentHighConfidenceVisualTimestampNs"), Is.EqualTo(1000 * Ms));
        Assert.That(_board.CurrentVisualPoseQuality, Is.EqualTo(DjiBoardVisionProvider.VisualPoseQuality.Degraded));
        Assert.That(Get("_consecutiveTranslationJumpSuspects"), Is.EqualTo(3));
    }

    [Test]
    public void RetentionDisabled_KeepsOriginalTwoHoldsThenBorderDegradation()
    {
        Step(1100, retention: false); Step(1200, retention: false);
        Assert.That(_board.CurrentVisualPoseQuality, Is.EqualTo(DjiBoardVisionProvider.VisualPoseQuality.HighConfidence));
        Assert.That(Get("_currentHighConfidenceVisualTimestampNs"), Is.EqualTo(1000 * Ms));
        Step(1300, retention: false);
        Assert.That(_board.CurrentVisualPoseQuality, Is.EqualTo(DjiBoardVisionProvider.VisualPoseQuality.Degraded));
    }

    [Test]
    public void Border48Reacquisition_TransitionsToContinuedRetentionUpdates()
    {
        BeginReacquisition();
        Step(1100); Step(1200); Step(1300); Step(1400); Step(1500);
        Assert.That(Get("_currentHighConfidenceVisualTimestampNs"), Is.EqualTo(1000 * Ms));
        Step(1550);
        Assert.That(Get("_currentHighConfidenceVisualTimestampNs"), Is.EqualTo(1550 * Ms));
        ResolveAndApply();
        Step(1650, x: .01f); ResolveAndApply();
        Assert.That(Get("_currentHighConfidenceVisualTimestampNs"), Is.EqualTo(1650 * Ms));
        Assert.That(_board.CurrentRenderPoseSource, Is.EqualTo(DjiRenderPoseSource.Visual));
        Assert.That(AppliedRecords(), Is.EqualTo(2));
    }

    [Test]
    public void AllThreeExperiments_LongGapRecoveryStillRequiresConfirmationAndDwell()
    {
        BeginReacquisition();
        Step(8724, jump: .2f, gap: 7.724f); Step(8824); Step(8924); Step(9024); Step(9124); Step(9173);
        Assert.That(Get("_currentHighConfidenceVisualTimestampNs"), Is.EqualTo(1000 * Ms));
        Step(9174); ResolveAndApply(); Step(9274, x: .01f); ResolveAndApply();
        Assert.That(Get("_currentHighConfidenceVisualTimestampNs"), Is.EqualTo(9274 * Ms));
        Assert.That(AppliedRecords(), Is.EqualTo(2));
        Assert.That(Get("reacquisitionStableTimeSeconds"), Is.EqualTo(.25f));
        Assert.That(Get("highConfidenceReacquisitionConfirmationFrames"), Is.EqualTo(3));
    }

    [Test]
    public void AllExperiments_DoNotBypassPoorReprojectionAfterLongGap()
    {
        BeginReacquisition();
        Assert.That(Reason(Evaluate(Metrics(timestamp: 8724, jump: .2f, gap: 7.724f, rms: 4))), Is.EqualTo("NORMALIZED_RMS"));
    }

    [Test]
    public void TagLossCanLeaveVisual_AndReturnRequiresFreshConfirmationAgain()
    {
        Step(1100, count: 3);
        Assert.That(_board.TryGetRenderCameraFromBoard(2f, out _, out var lost, out _), Is.False);
        Assert.That(lost, Is.Not.EqualTo(DjiRenderPoseSource.Visual));
        BeginReacquisition(); Step(1200); Step(1300);
        Assert.That(Get("_currentHighConfidenceVisualTimestampNs"), Is.EqualTo(1000 * Ms));
        Assert.That(Get("_hasVisualReacquisitionCandidate"), Is.False);
    }

    [Test]
    public void StaleInputDoesNotGainUnlimitedVisualLifetime()
    {
        Set("_lastHighConfidenceCameraFromBoardRealtime", Time.realtimeSinceStartup - 3f);
        Assert.That(_board.TryGetRenderCameraFromBoard(2f, out _, out var source, out _), Is.False);
        Assert.That(source, Is.Not.EqualTo(DjiRenderPoseSource.Visual));
    }

    [Test]
    public void InvalidNativePoseCannotBePromotedAndExistingStalenessFallbackRemains()
    {
        var result = NativeResult(1100);
        result.GetType().GetField("status").SetValue(result, -8);
        typeof(DjiBoardVisionProvider).GetMethod("ProcessResult", Private).Invoke(_board, new[] { result, (object)0L });
        Assert.That(Get("_currentHighConfidenceVisualTimestampNs"), Is.EqualTo(1000 * Ms));
        Set("_lastHighConfidenceCameraFromBoardRealtime", Time.realtimeSinceStartup - 3f);
        Assert.That(_board.TryGetRenderCameraFromBoard(2f, out _, out _, out _), Is.False);
    }

    [Test]
    public void DiagnosticsSeparateThresholdPromotionAndApplication()
    {
        var m = Metrics(); Evaluate(m); Set("_orientationMetrics", m);
        var r = new OrientationDiagnosticRecord { imageTimeNs = 1100 * Ms, recordType = "VisualOrientationCandidate" };
        typeof(DjiBoardVisionProvider).GetMethod("FillBorderRetentionDiagnostic", Private).Invoke(_board, new object[] { r });
        var json = DjiTelemetryValidationLogger.BuildOrientationDiagnosticJson("test", r);
        Assert.That(json, Does.Contain("\"aprilTagBorderRetentionExperimentEnabled\":true"));
        Assert.That(json, Does.Contain("\"activeBorderThresholdPx\":48"));
        Assert.That(json, Does.Contain("\"legacy64BorderEligible\":false"));
        Assert.That(json, Does.Contain("\"newVisualPosePromoted\":false"));
        Assert.That(json, Does.Contain("\"newVisualPoseApplied\":null"));
        r.hasBorderRetentionDiagnostics = false;
        Assert.That(DjiTelemetryValidationLogger.BuildOrientationDiagnosticJson("test", r), Does.Not.Contain("aprilTagBorderRetentionExperimentEnabled"));
    }

    private object Metrics(int timestamp = 1100, float border = 52, int count = 4, float x = 0,
        float jump = .01f, float gap = .1f, float rms = 1, float corner = 2, float rotation = 0) =>
        Activator.CreateInstance(MetricsType, new object[] { timestamp * Ms, count, count * 4, "0,1,2,3",
            rms, corner, 50f, rms / 50f, corner / 50f, border, 10000f,
            new Pose(new Vector3(x, 0, 2), Quaternion.identity), jump, rotation, gap, false, float.NaN, false, float.NaN });
    private object Evaluate(object metrics, bool border = true, bool stale = true, bool retention = true) =>
        typeof(DjiBoardVisionProvider).GetMethod("EvaluateVisualPoseQualityForAllExperiments", Private)
            .Invoke(_board, new[] { metrics, (object)border, stale, retention });
    private static string Reason(object e) => (string)e.GetType().GetField("reason").GetValue(e);
    private void Apply(object metrics, object e) => typeof(DjiBoardVisionProvider)
        .GetMethod("UpdateVisualPoseQuality", Private, null, new[] { MetricsType, e.GetType() }, null).Invoke(_board, new[] { metrics, e });
    private void Step(int timestamp, float x = 0, float jump = .01f, float gap = .1f, int count = 4, bool retention = true)
    {
        var m = Metrics(timestamp: timestamp, x: x, jump: jump, gap: gap, count: count);
        Apply(m, Evaluate(m, retention: retention));
        if (_board.CurrentVisualPoseQuality != DjiBoardVisionProvider.VisualPoseQuality.HighConfidence) return;
        var requires = (bool)typeof(DjiBoardVisionProvider).GetMethod("RequiresVisualReacquisitionHandover", Private).Invoke(_board, null);
        if (requires)
            typeof(DjiBoardVisionProvider).GetMethod("ObserveVisualReacquisitionCandidate", Private).Invoke(_board, new[] { NativeResult(timestamp), m, (object)false });
        else if (!(bool)Get("_holdingVisualAuthorityForBorderlineQuality") && !(bool)Get("_holdingVisualAuthorityForTranslationJumpSuspect"))
            typeof(DjiBoardVisionProvider).GetMethod("PromoteHighConfidenceVisualPose", Private).Invoke(_board,
                new[] { NativeResult(timestamp), m, (object)false, timestamp * Ms });
    }
    private object NativeResult(int timestamp)
    {
        var type = typeof(DjiBoardVisionProvider).GetNestedType("NativeBoardResult", BindingFlags.Public | BindingFlags.NonPublic);
        var r = Activator.CreateInstance(type);
        type.GetField("timestampNs").SetValue(r, timestamp * Ms);
        type.GetField("visionFrameTimestampNs").SetValue(r, timestamp * Ms);
        return r;
    }
    private void BeginReacquisition()
    {
        SetSource(DjiRenderPoseSource.TelemetryPropagated);
        SetQuality(DjiBoardVisionProvider.VisualPoseQuality.Degraded);
        Set("_visualReacquisitionMode", Enum.Parse(typeof(DjiBoardVisionProvider).GetNestedType("VisualReacquisitionMode", BindingFlags.NonPublic), "FreshRelocalization"));
    }
    private Pose ResolveAndApply()
    {
        Assert.That(_board.TryGetRenderCameraFromBoard(2f, out var pose, out var source, out _), Is.True);
        Assert.That(source, Is.EqualTo(DjiRenderPoseSource.Visual));
        typeof(DjiSharedReferenceCubeOverlay).GetMethod("ApplyStandaloneBoardOverlayPose", Private).Invoke(_overlay, new object[] { pose });
        // The build-only runtime hook calls this exact diagnostic after the
        // same production camera application; normal Editor flags stay off.
        typeof(DjiBoardVisionProvider).GetMethod("TraceBorderRetentionVisualApplication", Private).Invoke(_board, new object[] { pose });
        return pose;
    }
    private int AppliedRecords() => _output.ToString().Split('\n').Count(l => l.Contains("\"recordType\":\"VisualBorderRetentionPoseApplied\""));
    private object Get(string name) => typeof(DjiBoardVisionProvider).GetField(name, Private).GetValue(_board);
    private void Set(string name, object value) => typeof(DjiBoardVisionProvider).GetField(name, Private).SetValue(_board, value);
    private void SetSource(DjiRenderPoseSource source) => Set("<CurrentRenderPoseSource>k__BackingField", source);
    private void SetQuality(DjiBoardVisionProvider.VisualPoseQuality quality) => Set("<CurrentVisualPoseQuality>k__BackingField", quality);
    private void SetOverlay(string name, object value) => typeof(DjiSharedReferenceCubeOverlay).GetField(name, Private).SetValue(_overlay, value);

    public static void BuildCombinedDiagnosticApk()
    {
        var args = Environment.GetCommandLineArgs(); var index = Array.IndexOf(args, "-outputPath");
        if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("Missing -outputPath");
        var output = Path.GetFullPath(args[index + 1]);
        if (File.Exists(output)) throw new IOException("Refusing to overwrite APK.");
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android || EditorUserBuildSettings.buildAppBundle ||
            PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android) != "com.sok9hu" ||
            PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android) != ScriptingImplementation.IL2CPP ||
            PlayerSettings.Android.targetArchitectures != AndroidArchitecture.ARM64 ||
            (int)PlayerSettings.Android.minSdkVersion != 24 || (int)PlayerSettings.Android.targetSdkVersion != 36 ||
            !PlayerSettings.GetGraphicsAPIs(BuildTarget.Android).SequenceEqual(new[] { GraphicsDeviceType.OpenGLES3 }))
            throw new InvalidOperationException("Expected unchanged Android production configuration.");
        var defines = new[] { "DJI_APRILTAG_BORDER_48_EXPERIMENT", "DJI_APRILTAG_STALE_JUMP_EXPERIMENT",
            "DJI_APRILTAG_BORDER_RETENTION_EXPERIMENT", "DJI_VERTICAL_RAW_EXPERIMENT" };
        var persistent = PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.Android);
        if (defines.Any(persistent.Contains)) throw new InvalidOperationException("Experiments must remain build-only.");
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
            scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
            target = BuildTarget.Android, locationPathName = output, options = BuildOptions.None,
            extraScriptingDefines = defines });
        if (report.summary.result != BuildResult.Succeeded) throw new Exception("Combined experiment build failed.");
        Debug.Log("APRILTAG_COMBINED_APK_SUCCESS defines=" + string.Join(",", defines) + " path=" + output);
    }
}
