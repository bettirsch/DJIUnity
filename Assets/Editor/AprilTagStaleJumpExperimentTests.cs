using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

public sealed class AprilTagStaleJumpExperimentTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const long Ms = 1_000_000L;
    private GameObject _object;
    private DjiBoardVisionProvider _board;
    private Type MetricsType => typeof(DjiBoardVisionProvider).GetNestedType("VisualPoseMetrics", BindingFlags.NonPublic);
    private StaleVisualJumpCluster Cluster => (StaleVisualJumpCluster)Get("_staleJumpCluster");

    [SetUp]
    public void SetUp()
    {
        _object = new GameObject("Stale jump experiment test");
        _object.SetActive(false);
        _board = _object.AddComponent<DjiBoardVisionProvider>();
        SetMode("FreshRelocalization");
        Set("<CurrentRenderPoseSource>k__BackingField", DjiRenderPoseSource.TelemetryPropagated);
        Set("_hasLastVisualQualityPose", true);
        Set("_currentHighConfidenceVisualTimestampNs", 1000 * Ms);
        Set("<HasHighConfidenceCameraFromBoard>k__BackingField", true);
        Set("<CurrentHighConfidenceCameraFromBoard>k__BackingField", new Pose(new Vector3(-.2f, 0, 2), Quaternion.identity));
    }

    [TearDown]
    public void TearDown() => Object.DestroyImmediate(_object);

    [Test]
    public void NormalBuild_DoesNotEnableExperiment()
    {
        // Historical ID retained; OFF is only an explicit offline comparison.
        Assert.That(DjiAprilTagStaleJumpExperiment.Enabled, Is.True);
        Assert.That(Reason(Evaluate(Metrics(), stale: false)), Is.EqualTo("POSE_TRANSLATION_JUMP_SEVERE"));
        var normal = typeof(DjiBoardVisionProvider).GetMethod("EvaluateVisualPoseQuality", Private)
            .Invoke(_board, new[] { Metrics() });
        Assert.That(Reason(normal), Is.Empty);
        Assert.That(Cluster.Active, Is.True);
        Assert.That(Cluster.Count, Is.EqualTo(1));
        Assert.That(Get("_currentHighConfidenceVisualTimestampNs"), Is.EqualTo(1000 * Ms));
        Assert.That(Get("motionAwareImmediateTranslationJumpRejectMeters"), Is.EqualTo(.15f));
    }

    [TestCase(.1f)]
    [TestCase(2f)]
    public void ShortOrBoundaryGap_DoesNotRelaxEmergencyCap(float gap)
    {
        Assert.That(Reason(Evaluate(Metrics(gap: gap))), Is.EqualTo("POSE_TRANSLATION_JUMP_SEVERE"));
        Assert.That(Cluster.Active, Is.False);
    }

    [Test]
    public void LongGap20Cm_IsAllowedOnlyIntoConfirmation_NotAuthority()
    {
        Set("<CurrentVisualPoseQuality>k__BackingField", DjiBoardVisionProvider.VisualPoseQuality.HighConfidence);
        Step(8724, .2f, 7.724f);
        Assert.That(Cluster.Count, Is.EqualTo(1));
        Assert.That(_board.CurrentVisualPoseQuality, Is.EqualTo(DjiBoardVisionProvider.VisualPoseQuality.Degraded));
        Assert.That(Get("_consecutiveHighConfidenceVisualPoses"), Is.EqualTo(1));
        Assert.That(Get("_currentHighConfidenceVisualTimestampNs"), Is.EqualTo(1000 * Ms));
        Assert.That(_board.CurrentRenderPoseSource, Is.EqualTo(DjiRenderPoseSource.TelemetryPropagated));
        Assert.That(Get("_hasVisualReacquisitionCandidate"), Is.False);
    }

    [Test]
    public void StableFourTagCluster_CompletesOriginalThreeSamplesAnd250MsDwell()
    {
        Step(8724, .2f, 7.724f);
        Step(8824);
        Assert.That(Get("_hasVisualReacquisitionCandidate"), Is.False);
        Step(8924);
        Assert.That(Get("_hasVisualReacquisitionCandidate"), Is.True);
        Step(9024); Step(9124); Step(9173);
        Assert.That(Get("_currentHighConfidenceVisualTimestampNs"), Is.EqualTo(1000 * Ms));
        Step(9174);
        Assert.That(Get("_currentHighConfidenceVisualTimestampNs"), Is.EqualTo(9174 * Ms));
        Assert.That(_board.CurrentHighConfidenceCameraFromBoard.position, Is.EqualTo(new Vector3(0, 0, 2)));
        Assert.That(Cluster.Active, Is.False);
        Assert.That(Get("highConfidenceReacquisitionConfirmationFrames"), Is.EqualTo(3));
        Assert.That(Get("reacquisitionStableTimeSeconds"), Is.EqualTo(.25f));
        // Promotion is not itself a render-source commit.
        Assert.That(_board.CurrentRenderPoseSource, Is.EqualTo(DjiRenderPoseSource.TelemetryPropagated));
    }

    [TestCase(51f, true, true)]
    [TestCase(51f, false, false)]
    [TestCase(47f, true, false)]
    [TestCase(64f, false, true)]
    public void BorderAndStaleExperiments_ComposeWithoutBypassingEachOther(float border, bool borderEnabled, bool allowed)
    {
        var evaluation = Evaluate(Metrics(border: border), border: borderEnabled);
        Assert.That(string.IsNullOrEmpty(Reason(evaluation)), Is.EqualTo(allowed));
        Assert.That(Cluster.Active, Is.EqualTo(allowed));
    }

    [Test]
    public void CombinedExperiments_StillRequireActualHandoverDwell()
    {
        Step(8724, .2f, 7.724f, border: 51);
        Step(8824, border: 51); Step(8924, border: 51);
        Assert.That(Get("_currentHighConfidenceVisualTimestampNs"), Is.EqualTo(1000 * Ms));
        Step(9024, border: 51); Step(9124, border: 51); Step(9174, border: 51);
        Assert.That(Get("_currentHighConfidenceVisualTimestampNs"), Is.EqualTo(9174 * Ms));
    }

    [TestCase(4f, 2f, 0f, "NORMALIZED_RMS")]
    [TestCase(1f, 6f, 0f, "NORMALIZED_CORNER_RESIDUAL")]
    [TestCase(1f, 2f, 11f, "POSE_ROTATION_JUMP")]
    public void IndependentQualityGates_AreUnchanged(float rms, float corner, float rotation, string expected)
    {
        Assert.That(Reason(Evaluate(Metrics(rms: rms, corner: corner, rotation: rotation))), Is.EqualTo(expected));
        Assert.That(Cluster.Active, Is.False);
    }

    [Test]
    public void ActiveVisual_IsNeverRelaxedEvenWithLongTimestampGap()
    {
        Set("<CurrentRenderPoseSource>k__BackingField", DjiRenderPoseSource.Visual);
        Assert.That(Reason(Evaluate(Metrics())), Is.EqualTo("POSE_TRANSLATION_JUMP_SEVERE"));
    }

    [TestCase(2)]
    [TestCase(3)]
    public void FewerThanFourTags_KeepLegacyJumpGate(int count)
    {
        Assert.That(Reason(Evaluate(Metrics(count: count))), Is.EqualTo("POSE_TRANSLATION_JUMP"));
        Assert.That(Cluster.Active, Is.False);
    }

    [Test]
    public void InitialAcquisition_DoesNotGainStaleException()
    {
        SetMode("None");
        Assert.That(Reason(Evaluate(Metrics())), Is.EqualTo("POSE_TRANSLATION_JUMP_SEVERE"));
    }

    [Test]
    public void ContradictoryCluster_CancelsConfirmationAndCannotPromote()
    {
        Step(8724, .2f, 7.724f); Step(8824);
        var metrics = Metrics(timestamp: 8924, jump: .01f, gap: .1f, x: .04f);
        var evaluation = Evaluate(metrics);
        Assert.That(Reason(evaluation), Is.EqualTo("STALE_CLUSTER_INCONSISTENT"));
        Apply(metrics, evaluation);
        Assert.That(Get("_consecutiveHighConfidenceVisualPoses"), Is.EqualTo(0));
        Assert.That(Get("_currentHighConfidenceVisualTimestampNs"), Is.EqualTo(1000 * Ms));
    }

    [TestCase(8724)]
    [TestCase(8723)]
    [TestCase(8975)]
    public void DuplicateOutOfOrderOrSparseObservations_DoNotCount(int nextTimestamp)
    {
        Step(8724, .2f, 7.724f);
        var evaluation = Evaluate(Metrics(timestamp: nextTimestamp, jump: 0, gap: .1f));
        Assert.That(Reason(evaluation), Is.EqualTo("STALE_CLUSTER_TIMESTAMP_DISCONTINUITY"));
        Assert.That(Cluster.Count, Is.EqualTo(0));
    }

    [Test]
    public void PoorSampleDuringDwell_CancelsCandidate()
    {
        Step(8724, .2f, 7.724f); Step(8824); Step(8924);
        var bad = Metrics(timestamp: 9024, jump: 0, gap: .1f, rms: 4);
        Apply(bad, Evaluate(bad));
        Assert.That(Get("_hasVisualReacquisitionCandidate"), Is.False);
        Assert.That(Cluster.Active, Is.False);
    }

    [Test]
    public void MissingPoseDuringDwell_RequiresNewConfirmation()
    {
        Step(8724, .2f, 7.724f); Step(8824); Step(8924);
        typeof(DjiBoardVisionProvider).GetMethod("Reject", Private).Invoke(_board, new object[] { "NO_POSE" });
        Step(9024);
        Assert.That(Get("_hasVisualReacquisitionCandidate"), Is.False);
        Assert.That(Get("_consecutiveHighConfidenceVisualPoses"), Is.EqualTo(1));
    }

    [Test]
    public void ClusterCannotWalkAwayThroughSmallSteps()
    {
        Step(8724, .2f, 7.724f);
        Step(8824, x: .02f);
        Assert.That(Reason(Evaluate(Metrics(timestamp: 8924, jump: .02f, gap: .1f, x: .04f))),
            Is.EqualTo("STALE_CLUSTER_INCONSISTENT"));
    }

    [Test]
    public void ResetDoesNotLeakClusterIntoNextSession()
    {
        Step(8724, .2f, 7.724f);
        typeof(DjiBoardVisionProvider).GetMethod("ResetVisualLocalizationReferencesAfterTimeout", Private).Invoke(_board, null);
        Assert.That(Cluster.Active, Is.False);
        Assert.That(Cluster.Count, Is.EqualTo(0));
    }

    [Test]
    public void LoggingDistinguishesLegacyGateConfirmationAuthorityAndAppliedCorrection()
    {
        var metrics = Metrics();
        Apply(metrics, Evaluate(metrics));
        Set("_orientationMetrics", metrics);
        var record = new OrientationDiagnosticRecord { recordType = "VisualOrientationCandidate", imageTimeNs = 8724 * Ms,
            poseValid = true, hasQualityMetrics = true, renderAuthority = "TelemetryPropagated" };
        typeof(DjiBoardVisionProvider).GetMethod("FillStaleJumpDiagnostic", Private).Invoke(_board, new object[] { record });
        var json = DjiTelemetryValidationLogger.BuildOrientationDiagnosticJson("test", record);
        Assert.That(json, Does.Contain("\"legacyAbsoluteTranslationJumpRejected\":true"));
        Assert.That(json, Does.Contain("\"legacyTranslationJumpDecision\":\"Reject\""));
        Assert.That(json, Does.Contain("\"experimentalTranslationJumpDecision\":\"ALLOW_EXISTING_CONFIRMATION\""));
        Assert.That(json, Does.Contain("\"staleClusterConfirmationCount\":1"));
        Assert.That(json, Does.Contain("\"previousAcceptedVisualPoseAgeMs\":7724"));
        Assert.That(json, Does.Contain("\"renderAuthority\":\"TelemetryPropagated\""));
        Assert.That(json, Does.Not.Contain("appliedPositionCorrectionM"));
        Assert.That(Cluster.Count, Is.EqualTo(1));
        record.appliedCorrectionAvailable = true;
        record.previousAppliedCameraInBoard = Vector3.zero;
        record.appliedCameraInBoard = new Vector3(.2f, 0, 0);
        Assert.That(DjiTelemetryValidationLogger.BuildOrientationDiagnosticJson("test", record),
            Does.Contain("\"appliedPositionCorrectionM\":0.2"));
        record.hasStaleJumpDiagnostics = false;
        Assert.That(DjiTelemetryValidationLogger.BuildOrientationDiagnosticJson("test", record),
            Does.Not.Contain("aprilTagStaleJumpExperimentEnabled"));
    }

    private object Metrics(int timestamp = 8724, float jump = .2f, float gap = 7.724f, float x = 0,
        int count = 4, float border = 64, float rms = 1, float corner = 2, float rotation = 0) =>
        Activator.CreateInstance(MetricsType, new object[] { timestamp * Ms, count, count * 4, "0,1,2,3",
            rms, corner, 50f, rms / 50f, corner / 50f, border, 10000f,
            new Pose(new Vector3(x, 0, 2), Quaternion.identity), jump, rotation, gap,
            false, float.NaN, false, float.NaN });
    private object Evaluate(object metrics, bool border = true, bool stale = true) =>
        typeof(DjiBoardVisionProvider).GetMethod("EvaluateVisualPoseQualityForAllExperiments", Private)
            .Invoke(_board, new[] { metrics, (object)border, stale, false });
    private static string Reason(object evaluation) => (string)evaluation.GetType().GetField("reason").GetValue(evaluation);
    private void Apply(object metrics, object evaluation) => typeof(DjiBoardVisionProvider)
        .GetMethod("UpdateVisualPoseQuality", Private, null, new[] { MetricsType, evaluation.GetType() }, null)
        .Invoke(_board, new[] { metrics, evaluation });
    private void Step(int timestamp, float jump = 0, float gap = .1f, float x = 0, float border = 64)
    {
        var metrics = Metrics(timestamp, jump, gap, x, border: border);
        Apply(metrics, Evaluate(metrics));
        if (_board.CurrentVisualPoseQuality != DjiBoardVisionProvider.VisualPoseQuality.HighConfidence) return;
        var resultType = typeof(DjiBoardVisionProvider).GetNestedType("NativeBoardResult", BindingFlags.Public | BindingFlags.NonPublic);
        var result = Activator.CreateInstance(resultType);
        resultType.GetField("timestampNs").SetValue(result, timestamp * Ms);
        resultType.GetField("visionFrameTimestampNs").SetValue(result, timestamp * Ms);
        typeof(DjiBoardVisionProvider).GetMethod("ObserveVisualReacquisitionCandidate", Private)
            .Invoke(_board, new[] { result, metrics, (object)false });
    }
    private object Get(string name) => typeof(DjiBoardVisionProvider).GetField(name, Private).GetValue(_board);
    private void Set(string name, object value) => typeof(DjiBoardVisionProvider).GetField(name, Private).SetValue(_board, value);
    private void SetMode(string name) => Set("_visualReacquisitionMode", Enum.Parse(
        typeof(DjiBoardVisionProvider).GetNestedType("VisualReacquisitionMode", BindingFlags.NonPublic), name));
}
