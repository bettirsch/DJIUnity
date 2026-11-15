using System;
using System.Collections.Generic;
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

public sealed class AprilTagBorder48ExperimentTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const long Ms = 1_000_000L;
    private GameObject _object;
    private DjiBoardVisionProvider _board;
    private Type MetricsType => typeof(DjiBoardVisionProvider).GetNestedType("VisualPoseMetrics", BindingFlags.NonPublic);

    [SetUp]
    public void SetUp()
    {
        _object = new GameObject("Border experiment test");
        _object.SetActive(false);
        _board = _object.AddComponent<DjiBoardVisionProvider>();
        SetReacquisition(true);
    }

    [TearDown]
    public void TearDown() => Object.DestroyImmediate(_object);

    [Test]
    public void NormalEditorBuild_DoesNotEnableExperiment()
    {
        Assert.That(DjiAprilTagBorderExperiment.Enabled, Is.False);
        Assert.That(Reason(Evaluate(Metrics(51), false)), Is.EqualTo("BORDER_PROXIMITY"));
        Assert.That(Get("highConfidenceMinimumBorderDistancePixels"), Is.EqualTo(64f));
    }

    [TestCase(47.99f, false)]
    [TestCase(48f, true)]
    [TestCase(51f, true)]
    [TestCase(63.99f, true)]
    [TestCase(64f, true)]
    public void ExperimentalFourTagReacquisition_Uses48PixelBoundary(float border, bool passes)
    {
        Assert.That(string.IsNullOrEmpty(Reason(Evaluate(Metrics(border), true))), Is.EqualTo(passes));
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public void LowerTagCounts_RetainOriginalBorderPolicy(int count)
    {
        Assert.That(Reason(Evaluate(Metrics(51, count), true)), Is.EqualTo("BORDER_PROXIMITY"));
        Assert.That(Reason(Evaluate(Metrics(64, count), true)), Is.Empty);
    }

    [Test]
    public void InitialAcquisitionAndActiveVisual_DoNotReceiveRelaxedThreshold()
    {
        SetReacquisition(false);
        Assert.That(Reason(Evaluate(Metrics(51), true)), Is.EqualTo("BORDER_PROXIMITY"));
        Set("<CurrentVisualPoseQuality>k__BackingField", DjiBoardVisionProvider.VisualPoseQuality.HighConfidence);
        Assert.That(Reason(Evaluate(Metrics(51), true)), Is.EqualTo("BORDER_PROXIMITY"));
    }

    [TestCase(4f, 2f, .01f, 0f, "NORMALIZED_RMS")]
    [TestCase(1f, 6f, .01f, 0f, "NORMALIZED_CORNER_RESIDUAL")]
    [TestCase(1f, 2f, .16f, 0f, "POSE_TRANSLATION_JUMP_SEVERE")]
    [TestCase(1f, 2f, .01f, 11f, "POSE_ROTATION_JUMP")]
    public void OtherQualityGuards_RemainActive(float rms, float corner, float jump, float rotation, string reason)
    {
        Assert.That(Reason(Evaluate(Metrics(51, 4, rms, corner, jump, rotation), true)), Is.EqualTo(reason));
    }

    [Test]
    public void NormalProfile_IsIdenticalRegardlessOfReacquisitionContext()
    {
        foreach (var count in new[] { 1, 2, 3, 4 })
        foreach (var border in new[] { 20f, 47f, 48f, 51f, 63f, 64f, 300f })
        {
            SetReacquisition(true);
            var metrics = Metrics(border, count);
            var expected = Reason(Evaluate(metrics, false));
            SetReacquisition(false);
            Assert.That(Reason(Evaluate(metrics, false)), Is.EqualTo(expected));
            Assert.That(DjiAprilTagBorderExperiment.Resolve(64f, count, true, false), Is.EqualTo(64f));
        }
    }

    [Test]
    public void Experimental51PixelPose_GainsHighConfidenceRatherThanOnlyHoldingOldVisual()
    {
        for (int i = 1; i <= 3; i++)
        {
            var metrics = Metrics(51, timestampMs: i * 100);
            Apply(metrics, Evaluate(metrics, true));
            Assert.That(_board.CurrentVisualPoseQuality == DjiBoardVisionProvider.VisualPoseQuality.HighConfidence,
                Is.EqualTo(i == 3));
        }
        Assert.That(Get("_holdingVisualAuthorityForBorderlineQuality"), Is.False);
        Assert.That(Get("_hasVisualReacquisitionCandidate"), Is.False);
        Assert.That(_board.HasHighConfidenceCameraFromBoard, Is.False);
    }

    [Test]
    public void ConfirmationAndActualHandoverDwell_AreNotBypassed()
    {
        var mode = typeof(DjiBoardVisionProvider).GetNestedType("VisualReacquisitionMode", BindingFlags.NonPublic);
        Set("_visualReacquisitionMode", Enum.Parse(mode, "PropagatedReacquisition"));
        Set("_shadowPropagationActive", true);
        Set("_hasLatestShadowPrediction", true);
        Set("_hasShadowRotationPropagationBaseline", true);
        Set("<CurrentRenderPoseSource>k__BackingField", DjiRenderPoseSource.TelemetryPropagated);
        var baselineType = typeof(DjiBoardVisionProvider).GetNestedType("ShadowRotationPropagationBaseline", BindingFlags.NonPublic);
        Set("_shadowRotationPropagationBaseline", Activator.CreateInstance(baselineType, new object[] {
            100 * Ms, new Pose(new Vector3(0, 0, 2), Quaternion.identity), Quaternion.identity, false,
            default(TelemetryAircraftPositionSample) }));
        var telemetry = _object.AddComponent<DjiCameraPoseProvider>();
        typeof(DjiCameraPoseProvider).GetField("<HasTelemetryOrientation>k__BackingField", Private).SetValue(telemetry, true);
        var history = (List<DjiCameraPoseProvider.TelemetryOrientationSample>)typeof(DjiCameraPoseProvider)
            .GetField("_telemetryOrientationHistory", Private).GetValue(telemetry);
        for (var timestampMs = 300; timestampMs <= 600; timestampMs += 100)
            history.Add(new DjiCameraPoseProvider.TelemetryOrientationSample(timestampMs * Ms, Quaternion.identity, true, 0, 0));
        Set("_telemetryProvider", telemetry);
        for (int i = 1; i <= 3; i++)
        {
            var metrics = Metrics(51, timestampMs: i * 100);
            Apply(metrics, Evaluate(metrics, true));
        }
        Observe(300);
        Assert.That(_board.HasHighConfidenceCameraFromBoard, Is.False);
        Observe(549);
        Assert.That(_board.HasHighConfidenceCameraFromBoard, Is.False);
        Observe(550);
        Assert.That(_board.HasHighConfidenceCameraFromBoard, Is.True);
        Assert.That(_board.CurrentHighConfidenceCameraFromBoard.position, Is.EqualTo(new Vector3(0, 0, 2)));
    }

    [Test]
    public void ReturningToVisual_RestoresOriginal64PixelPolicy()
    {
        Assert.That(Reason(Evaluate(Metrics(51), true)), Is.Empty);
        SetReacquisition(false);
        Assert.That(Reason(Evaluate(Metrics(51), true)), Is.EqualTo("BORDER_PROXIMITY"));
    }

    [Test]
    public void ExperimentalDiagnostics_PreserveThresholdConfirmationDwellAndPosition()
    {
        var r = new OrientationDiagnosticRecord { recordType = "VisualOrientationCandidate", poseValid = true,
            hasBorderExperimentDiagnostics = true, borderExperimentEnabled = true, borderReacquisition = true,
            activeBorderThresholdPixels = 48, borderEligible = true, borderMetricsAvailable = true, qualityConfirmationCount = 2,
            qualityConfirmationRequired = 3, reacquisitionDwellMilliseconds = 249,
            requiredReacquisitionDwellMilliseconds = 250, candidatePosition = new Vector3(1, 2, 3) };
        var json = DjiTelemetryValidationLogger.BuildOrientationDiagnosticJson("test", r);
        Assert.That(json, Does.Contain("\"aprilTagBorder48ExperimentEnabled\":true"));
        Assert.That(json, Does.Contain("\"activeBorderThresholdPx\":48"));
        Assert.That(json, Does.Contain("\"qualityConfirmationCount\":2"));
        Assert.That(json, Does.Contain("\"reacquisitionDwellMs\":249"));
        Assert.That(json, Does.Contain("\"candidateCameraFromBoardPosition\":{\"x\":1,\"y\":2,\"z\":3}"));
        r.hasBorderExperimentDiagnostics = false;
        Assert.That(DjiTelemetryValidationLogger.BuildOrientationDiagnosticJson("test", r),
            Does.Not.Contain("aprilTagBorder48ExperimentEnabled"));
    }

    [Test]
    public void MissingPoseMetrics_DoNotClaimBorderEligibilityOrZeroMotion()
    {
        var r = new OrientationDiagnosticRecord { recordType = "VisualOrientationCandidate",
            hasBorderExperimentDiagnostics = true, borderExperimentEnabled = true, activeBorderThresholdPixels = 64 };
        var json = DjiTelemetryValidationLogger.BuildOrientationDiagnosticJson("test", r);
        Assert.That(json, Does.Contain("\"borderEligible\":null"));
        Assert.That(json, Does.Contain("\"candidateTranslationJumpM\":null"));
        Assert.That(json, Does.Contain("\"candidateCameraFromBoardPosition\":{\"x\":null,\"y\":null,\"z\":null}"));
    }

    private object Metrics(float border, int count = 4, float rms = 1f, float corner = 2f,
        float jump = .01f, float rotation = 0f, int timestampMs = 100) => Activator.CreateInstance(MetricsType,
            new object[] { timestampMs * Ms, count, count * 4, "0,1,2,3", rms, corner, 50f,
                rms / 50f, corner / 50f, border, 10000f, new Pose(new Vector3(0, 0, 2), Quaternion.identity),
                jump, rotation, .1f, false, float.NaN, false, float.NaN });

    private object Evaluate(object metrics, bool enabled) => typeof(DjiBoardVisionProvider)
        .GetMethod("EvaluateVisualPoseQualityForBorderProfile", Private).Invoke(_board, new[] { metrics, (object)enabled });
    private static string Reason(object evaluation) => (string)evaluation.GetType().GetField("reason").GetValue(evaluation);
    private void Apply(object metrics, object evaluation) => typeof(DjiBoardVisionProvider)
        .GetMethod("UpdateVisualPoseQuality", Private, null, new[] { MetricsType, evaluation.GetType() }, null)
        .Invoke(_board, new[] { metrics, evaluation });
    private object Get(string name) => typeof(DjiBoardVisionProvider).GetField(name, Private).GetValue(_board);
    private void Set(string name, object value) => typeof(DjiBoardVisionProvider).GetField(name, Private).SetValue(_board, value);
    private void SetReacquisition(bool enabled)
    {
        var type = typeof(DjiBoardVisionProvider).GetNestedType("VisualReacquisitionMode", BindingFlags.NonPublic);
        Set("_visualReacquisitionMode", Enum.Parse(type, enabled ? "FreshRelocalization" : "None"));
    }
    private void Observe(int timestampMs)
    {
        var resultType = typeof(DjiBoardVisionProvider).GetNestedType("NativeBoardResult", BindingFlags.Public | BindingFlags.NonPublic);
        var result = Activator.CreateInstance(resultType);
        resultType.GetField("timestampNs").SetValue(result, timestampMs * Ms);
        resultType.GetField("visionFrameTimestampNs").SetValue(result, timestampMs * Ms);
        typeof(DjiBoardVisionProvider).GetMethod("ObserveVisualReacquisitionCandidate", Private)
            .Invoke(_board, new[] { result, Metrics(51, timestampMs: timestampMs), (object)false });
    }

    public static void BuildDiagnosticApk()
    {
        var args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, "-outputPath");
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
        if (PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.Android).Contains("DJI_APRILTAG_BORDER_48_EXPERIMENT"))
            throw new InvalidOperationException("Experiment must not be a persistent project define.");
        var defines = new[] { "DJI_VERTICAL_RAW_EXPERIMENT", "DJI_APRILTAG_BORDER_48_EXPERIMENT" };
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
            scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
            target = BuildTarget.Android, locationPathName = output, options = BuildOptions.None,
            extraScriptingDefines = defines });
        if (report.summary.result != BuildResult.Succeeded) throw new Exception("Border experiment APK build failed.");
        Debug.Log("APRILTAG_BORDER_48_APK_SUCCESS defines=" + string.Join(",", defines) + " path=" + output);
    }
}
