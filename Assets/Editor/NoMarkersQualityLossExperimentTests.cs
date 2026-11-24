using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

public sealed class NoMarkersQualityLossExperimentTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const long Ms = 1_000_000;
    private GameObject _root, _cameraRoot, _overlayRoot, _content;
    private DjiBoardVisionProvider _board;
    private Pose _visual;
    private long _baseline;
    private Camera _camera;
    private DjiSharedReferenceCubeOverlay _overlay;

    [Serializable] private sealed class Replay
    {
        public long baselineTimestampNs;
        public Vector3 position;
        public Quaternion rotation;
        public ReplayFrame[] frames;
        public Quaternion navigationFromCamera;
        public ReplayApplication[] applications;
        public ReplayPosition[] positions;
    }
    [Serializable] private sealed class ReplayApplication
    {
        public long timestampNs, attitudeTimestampNs;
        public Quaternion navigationFromCamera;
    }
    [Serializable] private sealed class ReplayPosition
    {
        public long timestampNs;
        public double latitude, longitude, altitude;
        public int satelliteCount;
    }
    [Serializable] private sealed class ReplayFrame
    {
        public long sourceNs, arrivalNs;
        public int status, markerCount, sequence;
    }
    [Serializable] private sealed class NativeFixture { public DjiBoardVisionProvider.NativeBoardResult result; }

    [SetUp] public void SetUp()
    {
        _root = new GameObject("No-markers quality-loss test"); _root.SetActive(false);
        _board = _root.AddComponent<DjiBoardVisionProvider>();
        _visual = new Pose(new Vector3(.1f, -.2f, 2f), Quaternion.Euler(170, 2, 1));
        _baseline = 1000 * Ms;
        SeedVisual(_visual, _baseline);
        _cameraRoot = new GameObject("Applied camera", typeof(Camera)); _camera = _cameraRoot.GetComponent<Camera>();
        _overlayRoot = new GameObject("Overlay"); _overlayRoot.SetActive(false);
        _overlay = _overlayRoot.AddComponent<DjiSharedReferenceCubeOverlay>();
        _content = new GameObject("Fixed model"); _content.transform.position = new Vector3(3, 4, 5);
        foreach (var pair in new (string, object)[] { ("_boardVisionProvider", _board), ("_overlayCamera", _camera), ("_contentRoot", _content) })
            typeof(DjiSharedReferenceCubeOverlay).GetField(pair.Item1, Private).SetValue(_overlay, pair.Item2);
    }

    [TearDown] public void TearDown()
    {
        Object.DestroyImmediate(_overlayRoot); Object.DestroyImmediate(_cameraRoot);
        Object.DestroyImmediate(_content); Object.DestroyImmediate(_root);
    }

    [TestCase(false, -8, 0, false)] [TestCase(true, -8, 0, true)]
    [TestCase(true, 0, 0, true)] [TestCase(false, 0, 0, true)]
    [TestCase(true, -8, 4, false)]
    [TestCase(true, -1, 0, false)] [TestCase(true, -2, 0, false)]
    [TestCase(true, -3, 0, false)] [TestCase(true, -4, 0, false)]
    [TestCase(true, -5, 0, false)] [TestCase(true, -6, 0, false)]
    [TestCase(true, -7, 0, false)] [TestCase(true, -9, 0, false)]
    [TestCase(true, -10, 0, false)] [TestCase(true, -11, 0, false)]
    [TestCase(true, -12, 0, false)] [TestCase(true, -13, 0, false)]
    public void OnlyProvenZeroDetectionChangesQuality(bool enabled, int status, int count, bool lost)
    {
        // The false cases are offline legacy comparisons, never shipping profiles.
        Process(Result(_baseline + Ms, status, count), enabled);
        Assert.That(_board.CurrentVisualPoseQuality, Is.EqualTo(lost
            ? DjiBoardVisionProvider.VisualPoseQuality.Unavailable : DjiBoardVisionProvider.VisualPoseQuality.HighConfidence));
        AssertBaseline();
    }

    [Test] public void NoCallback_DoesNotSynthesizeQualityLoss()
    {
        Assert.That(_board.TryGetRenderCameraFromBoard(2, out var pose, out var source, out _), Is.True);
        Assert.That(source, Is.EqualTo(DjiRenderPoseSource.Visual)); AssertPose(pose, _visual);
        Assert.That(Get("_propagationStartTimestampNs"), Is.EqualTo(0L));
    }

    [TestCase(1)] [TestCase(40)]
    public void ZeroDetection_PreservesExactAcceptedPoseAndBaseline(int count)
    {
        for (var i = 1; i <= count; i++) Process(Result(_baseline + i * Ms, sequence: i), true);
        AssertBaseline();
        Assert.That(_board.CurrentVisualPoseQuality, Is.EqualTo(DjiBoardVisionProvider.VisualPoseQuality.Unavailable));
        Assert.That(Get("_propagationStartTimestampNs"), Is.EqualTo(0L), "Only render authority starts the clock.");
    }

    [Test] public void InitialWaiting_DoesNotInventBaselineOrTelemetry()
    {
        Set("<HasHighConfidenceCameraFromBoard>k__BackingField", false);
        Set("_hasShadowRotationPropagationBaseline", false);
        Set("_currentHighConfidenceVisualTimestampNs", 0L);
        Process(Result(_baseline + Ms), true);
        Assert.That(_board.TryGetRenderCameraFromBoard(2, out _, out var source, out _), Is.False);
        Assert.That(source, Is.EqualTo(DjiRenderPoseSource.None));
        Assert.That(Get("_hasShadowRotationPropagationBaseline"), Is.False);
        Assert.That(DjiTrackingStatusIndicator.Resolve(source, false, true), Is.EqualTo(DjiTrackingStatus.Waiting));
    }

    [TestCase(-1)] [TestCase(0)]
    public void OldOrSameVisualTimestamp_CannotDowngradeNewVisual(int offset)
    {
        Process(Result(_baseline + offset * Ms), true);
        Assert.That(_board.CurrentVisualPoseQuality, Is.EqualTo(DjiBoardVisionProvider.VisualPoseQuality.HighConfidence));
        AssertBaseline();
    }

    [TestCase(2, 10, 3, 1, true)]
    [TestCase(2, 10, 1, 11, false)]
    [TestCase(2, 10, 2, 10, false)]
    [TestCase(2, 10, 2, 9, false)]
    [TestCase(2, 10, 2, 11, true)]
    [TestCase(0, 10, 0, 1, true)]
    public void GenerationAndSequenceFence(long observedGeneration, int observedSequence, long generation, int sequence, bool applies)
    {
        var order = new NoMarkersFrameOrder(); order.Observe(200 * Ms, observedGeneration, observedSequence);
        Assert.That(order.Evaluate(210 * Ms, generation, sequence, 100 * Ms) == NoMarkersQualityLossDecision.Applied, Is.EqualTo(applies));
        Assert.That(order.Evaluate(199 * Ms, generation, sequence, 100 * Ms), Is.Not.EqualTo(NoMarkersQualityLossDecision.Applied));
    }

    [Test] public void NewerNonZeroResultFencesDelayedNoPose_WithoutChangingItsLegacyPath()
    {
        Process(Result(_baseline + 20 * Ms, -6, 0, 20), true);
        Process(Result(_baseline + 10 * Ms, sequence: 10), true);
        Assert.That(_board.CurrentVisualPoseQuality, Is.EqualTo(DjiBoardVisionProvider.VisualPoseQuality.HighConfidence));
    }

    [Test] public void FutureSource_IsNotAQualityLoss()
    {
        Call("ProcessResultWithQualityLossExperiment", Result(_baseline + Ms), _baseline, true);
        Assert.That(_board.CurrentVisualPoseQuality, Is.EqualTo(DjiBoardVisionProvider.VisualPoseQuality.HighConfidence));
    }

    [Test] public void VisualLoss_ImmediatelySelectsRotationPropagation_AndAppliesSameCameraPose()
    {
        AddTelemetry(); Process(Result(_baseline + Ms), true);
        Assert.That(_board.TryGetRenderCameraFromBoard(2, out var applied, out var source, out _), Is.True);
        Assert.That(source, Is.EqualTo(DjiRenderPoseSource.TelemetryPropagated)); AssertPose(applied, _visual);
        // Android's clock intentionally returns zero in Editor. Verify timing
        // separately with explicit monotonic inputs, not an invented live clock.
        Assert.That(RotationPropagationTiming.StartIfNeeded(0, 1100 * Ms), Is.EqualTo(1100 * Ms));
        ApplyAndAssertCamera(applied);
        Assert.That(_content.transform.position, Is.EqualTo(Vector3.zero), "Existing board-root conversion is preserved.");
        Assert.That(DjiTrackingStatusIndicator.Resolve(source, true, true), Is.EqualTo(DjiTrackingStatus.TelemetryLimited));
        AssertBaseline();
    }

    [Test] public void LongLoss_StillUsesExistingTwoSecondTelemetryTransition()
    {
        AddTelemetry(); Process(Result(_baseline + Ms), true);
        _board.TryGetRenderCameraFromBoard(2, out _, out _, out _);
        var age = RotationPropagationTiming.AgeSeconds(3200 * Ms, 1100 * Ms);
        var source = RotationPropagationRenderPolicy.Resolve(false, 3, 2, true, true, age, 2, true);
        Assert.That(source, Is.EqualTo(DjiRenderPoseSource.TelemetryPropagated));
        ApplyAndAssertCamera(_visual);
        AssertBaseline();
    }

    [Test] public void MissingTelemetry_QualityLossDoesNotCreatePrediction()
    {
        Process(Result(_baseline + Ms), true);
        Assert.That(_board.TryGetRenderCameraFromBoard(2, out _, out var source, out _), Is.False);
        Assert.That(source, Is.EqualTo(DjiRenderPoseSource.None)); AssertBaseline();
    }

    [Test] public void StaleAttitude_HoldsRotation_WithoutFabricatingPositionMotion()
    {
        AddTelemetry(_baseline - 500 * Ms); Process(Result(_baseline + Ms), true);
        Assert.That(_board.TryGetRenderCameraFromBoard(2, out var pose, out _, out _), Is.True);
        AssertPose(pose, _visual); AssertBaseline();
    }

    [Test]
    public void SelectedMultiStartPose_ReacquiresOnlyAfterExistingConfirmationAndDwell()
    {
        var fixture = JsonUtility.FromJson<NativeFixture>(File.ReadAllText(Path.Combine(
            RequireReplayDirectory("DJI_MULTISTART_FIXTURE_DIR"), "selected-native-unity-fixture.json")));
        var args = new object[] { fixture.result, Pose.identity };
        Assert.That((bool)Call("TryGetCameraFromBoard", args), Is.True);
        _visual = (Pose)args[1]; _baseline = 500 * Ms; SeedVisual(_visual, _baseline);
        AddTelemetry();
        Process(Result(600 * Ms), true);
        Set("<CurrentRenderPoseSource>k__BackingField", DjiRenderPoseSource.TelemetryPropagated);
        Set("_shadowPropagationActive", true); Set("_hasLatestShadowPrediction", true);
        Set("_latestShadowPredictedCameraFromBoard", _visual);
        for (var t = 1000; t <= 1500; t += 100)
        {
            var result = JsonUtility.FromJson<DjiBoardVisionProvider.NativeBoardResult>(JsonUtility.ToJson(fixture.result));
            result.timestampNs = result.visionFrameTimestampNs = t * Ms; result.visionFrameId = result.frameSequence = t;
            Process(result, true);
            if (t < 1500) Assert.That(Get("_currentHighConfidenceVisualTimestampNs"), Is.EqualTo(_baseline), "No confirmation/dwell bypass.");
        }
        Assert.That(Get("_currentHighConfidenceVisualTimestampNs"), Is.EqualTo(1500 * Ms));
        Assert.That(_board.TryGetRenderCameraFromBoard(2, out var accepted, out var finalSource, out _), Is.True);
        Assert.That(finalSource, Is.EqualTo(DjiRenderPoseSource.Visual)); AssertPose(accepted, _visual);
        ApplyAndAssertCamera(accepted);
    }

    [TestCase(1.999f, 1.1f, 0f, 250, true, true, true, false)]
    [TestCase(2.000f, 1.1f, 0f, 250, true, true, true, false)]
    [TestCase(2.000f, 0f, 6.1f, 250, true, true, true, false)]
    [TestCase(2.000f, 0f, 0f, 250, true, false, true, false)]
    [TestCase(2.000f, 0f, 0f, 250, true, true, true, true)]
    [TestCase(2.001f, 1.1f, 6.1f, 250, true, false, true, true)]
    [TestCase(2.001f, 1.1f, 6.1f, 249, true, false, true, false)]
    [TestCase(2.001f, 1.1f, 6.1f, 250, false, false, true, false)]
    [TestCase(2.001f, 0f, 0f, 250, true, true, false, false)]
    public void UnifiedTelemetryHandover_UsesElapsedAgeAndPreservesOutageClock(
        float age, float distance, float angle, int dwellMs, bool highConfidence,
        bool hasBracket, bool hasLongLivedPrediction, bool accepted)
    {
        // Preserve the original offline handover contract, including its long-
        // outage rotation bypass. Final validated behavior has separate cases below.
        Set("_validatedTrackingSafetyEnabled", false);
        AssertTelemetryHandover(age, distance, angle, dwellMs, highConfidence,
            hasBracket, hasLongLivedPrediction, accepted);
    }

    private void AssertTelemetryHandover(
        float age, float distance, float angle, int dwellMs, bool highConfidence,
        bool hasBracket, bool hasLongLivedPrediction, bool accepted)
    {
        AddTelemetry();
        Set("<CurrentRenderPoseSource>k__BackingField", DjiRenderPoseSource.TelemetryPropagated);
        Set("_shadowPropagationActive", true);
        Set("_hasLatestShadowPrediction", hasLongLivedPrediction);
        const long propagationStartNs = 1100 * Ms;
        Set("_propagationStartTimestampNs", propagationStartNs);
        var timestamp = (hasBracket ? 1500 : 1700) * Ms;
        var candidate = new Pose(_visual.position + Vector3.right * distance,
            _visual.rotation * Quaternion.AngleAxis(angle, Vector3.up));
        var providerType = typeof(DjiBoardVisionProvider);
        var metricsType = providerType.GetNestedType("VisualPoseMetrics", BindingFlags.NonPublic);
        var metrics = Activator.CreateInstance(metricsType, new object[] {
            timestamp, 4, 16, "0,1,2,3", 1f, 1f, 100f, .01f, .01f, 100f, 10000f,
            candidate, 0f, 0f, .1f, false, 0f, false, 0f });
        Set("<CurrentVisualPoseQuality>k__BackingField", highConfidence
            ? DjiBoardVisionProvider.VisualPoseQuality.HighConfidence : DjiBoardVisionProvider.VisualPoseQuality.Degraded);
        var modeType = providerType.GetNestedType("VisualReacquisitionMode", BindingFlags.NonPublic);
        Set("_visualReacquisitionMode", Enum.Parse(modeType, "PropagatedReacquisition"));
        Set("_hasVisualReacquisitionCandidate", true);
        Set("_visualReacquisitionStartTimestampNs", timestamp - dwellMs * Ms);
        Set("_visualReacquisitionCandidateResult", Result(timestamp, 0, 4));
        Set("_visualReacquisitionCandidateMetrics", metrics);

        if ((bool)Get("_validatedTrackingSafetyEnabled"))
        {
            // Seed two actually checked source samples, not a pre-aged dwell
            // timestamp that could grant credit to one unverified frame.
            var firstNs = timestamp - dwellMs * Ms;
            var firstMetrics = Activator.CreateInstance(metricsType, new object[] {
                firstNs, 4, 16, "0,1,2,3", 1f, 1f, 100f, .01f, .01f, 100f, 10000f,
                candidate, 0f, 0f, .1f, false, 0f, false, 0f });
            Call("OfferVisualReacquisitionCandidate", Result(firstNs, 0, 4), firstMetrics, false, firstNs);
            Call("TryCompleteVisualReacquisitionHandoverAtTime", age, firstNs);
            Call("OfferVisualReacquisitionCandidate", Result(timestamp, 0, 4), metrics, false, timestamp);
            Call("TryCompleteVisualReacquisitionHandoverAtTime", age, timestamp);
        }
        else Call("TryCompleteVisualReacquisitionHandoverAtAge", age);

        Assert.That(Get("_propagationStartTimestampNs"), Is.EqualTo(propagationStartNs));
        var safetyCancelled = (bool)Get("_validatedTrackingSafetyEnabled") && hasBracket &&
            (angle > 6f || (age <= 2f && distance > 1f));
        Assert.That(Get("_hasVisualReacquisitionCandidate"), Is.EqualTo(!accepted && !safetyCancelled));
        Assert.That(Get("_currentHighConfidenceVisualTimestampNs"), Is.EqualTo(accepted ? timestamp : _baseline));
        if (accepted)
        {
            Assert.That(_board.TryGetRenderCameraFromBoard(2, out var rendered, out var source, out _), Is.True);
            Assert.That(source, Is.EqualTo(DjiRenderPoseSource.Visual));
            AssertPose(rendered, candidate);
        }
        else
        {
            Assert.That(_board.CurrentRenderPoseSource, Is.EqualTo(DjiRenderPoseSource.TelemetryPropagated));
            Call("CancelVisualReacquisition", "TEST_REJECTED_VISUAL_CANDIDATE");
            Assert.That(Get("_propagationStartTimestampNs"), Is.EqualTo(propagationStartNs));
        }
    }

    [TestCase(1.999f, 0f, 0f, 250, true, true)]
    [TestCase(2f, 1.1f, 0f, 250, true, false)]
    [TestCase(2.001f, 10f, 0f, 250, true, true)]
    [TestCase(30f, 10f, 0f, 249, true, false)]
    [TestCase(30f, 10f, 0f, 250, true, true)]
    [TestCase(30f, 0f, 45f, 250, true, false)]
    [TestCase(30f, 0f, 90f, 250, true, false)]
    [TestCase(30f, 0f, 90f, 1000, true, false)]
    [TestCase(30f, 0f, 0f, 250, false, false)]
    public void RestoredCandidate_LongOutageChecksRotationButAllowsLargePositionCorrection(
        float age, float distance, float angle, int dwell, bool hasBracket, bool accepted)
    {
        Assert.That(Get("_validatedTrackingSafetyEnabled"), Is.True, "Normal providers require validated tracking.");
        AssertTelemetryHandover(
            age, distance, angle, dwell, true, hasBracket, true, accepted);
    }

    [Test]
    public void RestoredCandidate_ExecutedMultiStartResultTraversesActualProcessAndHandover()
    {
        Assert.That(Get("_validatedTrackingSafetyEnabled"), Is.True);
        SelectedMultiStartPose_ReacquiresOnlyAfterExistingConfirmationAndDwell();
    }

    [Test]
    public void RestoredCandidate_StalePositiveDoesNotRejuvenateVisualOrReplaceBaseline()
    {
        Assert.That(Get("_validatedTrackingSafetyEnabled"), Is.True);
        var fixture = JsonUtility.FromJson<NativeFixture>(File.ReadAllText(Path.Combine(
            RequireReplayDirectory("DJI_MULTISTART_FIXTURE_DIR"), "selected-native-unity-fixture.json")));
        var result = fixture.result;
        result.timestampNs = result.visionFrameTimestampNs = _baseline + Ms;
        Call("ProcessResultWithQualityLossExperiment", result, _baseline + 3000 * Ms, true);
        AssertBaseline();
        Assert.That(Get("_hasVisualReacquisitionCandidate"), Is.False);
        Assert.That(_board.CurrentVisualPoseQuality, Is.EqualTo(DjiBoardVisionProvider.VisualPoseQuality.HighConfidence));
    }

    [Test]
    public void RestoredCandidate_AlternatingWrongBranchesCannotContributeToCorrectDwell()
    {
        Assert.That(Get("_validatedTrackingSafetyEnabled"), Is.True);
        AddTelemetry();
        var telemetry = (DjiCameraPoseProvider)Get("_telemetryProvider");
        var history = (List<DjiCameraPoseProvider.TelemetryOrientationSample>)typeof(DjiCameraPoseProvider)
            .GetField("_telemetryOrientationHistory", Private).GetValue(telemetry);
        foreach (var t in new[] { 1600, 1650 })
            history.Add(new DjiCameraPoseProvider.TelemetryOrientationSample(t * Ms, Quaternion.identity, true, 0, 0));
        Set("<CurrentRenderPoseSource>k__BackingField", DjiRenderPoseSource.TelemetryPropagated);
        Set("_shadowPropagationActive", true); Set("_hasLatestShadowPrediction", true);
        var mode = typeof(DjiBoardVisionProvider).GetNestedType("VisualReacquisitionMode", BindingFlags.NonPublic);
        Set("_visualReacquisitionMode", Enum.Parse(mode, "PropagatedReacquisition"));
        var metricsType = typeof(DjiBoardVisionProvider).GetNestedType("VisualPoseMetrics", BindingFlags.NonPublic);
        foreach (var step in new[] { (1100, 45f), (1200, 0f), (1300, 90f), (1400, 0f), (1500, 0f), (1600, 0f), (1650, 0f) })
        {
            var ns = step.Item1 * Ms;
            var pose = new Pose(_visual.position + Vector3.right * 10,
                _visual.rotation * Quaternion.AngleAxis(step.Item2, Vector3.up));
            var metrics = Activator.CreateInstance(metricsType, new object[] {
                ns, 4, 16, "0,1,2,3", 1f, 1f, 100f, .01f, .01f, 100f, 10000f,
                pose, 0f, 0f, .1f, false, 0f, false, 0f });
            if (!(bool)Get("_hasVisualReacquisitionCandidate")) Set("_visualReacquisitionStartTimestampNs", ns);
            Set("_hasVisualReacquisitionCandidate", true);
            Set("_visualReacquisitionCandidateMetrics", metrics);
            Set("_visualReacquisitionCandidateResult", Result(ns, 0, 4));
            Call("OfferVisualReacquisitionCandidate", Result(ns, 0, 4), metrics, false, ns);
            Call("TryCompleteVisualReacquisitionHandoverAtTime", 30f, ns);
            Assert.That(Get("_currentHighConfidenceVisualTimestampNs"), Is.EqualTo(step.Item1 == 1650 ? ns : _baseline));
        }
    }

    [Test]
    public void InvalidOrientation_DropsUnifiedRenderAuthorityToNone()
    {
        AddTelemetry();
        Process(Result(_baseline + Ms), true);
        Assert.That(_board.TryGetRenderCameraFromBoard(2, out _, out var startSource, out _), Is.True);
        Assert.That(startSource, Is.EqualTo(DjiRenderPoseSource.TelemetryPropagated));
        Set("_propagationStartTimestampNs", _baseline + Ms);

        var telemetry = (DjiCameraPoseProvider)Get("_telemetryProvider");
        typeof(DjiCameraPoseProvider).GetField("<HasTelemetryOrientation>k__BackingField", Private)
            .SetValue(telemetry, false);
        Call("OnTelemetryOrientationRecorded",
            new DjiCameraPoseProvider.TelemetryOrientationSample(_baseline + 2 * Ms, Quaternion.identity, false, 0, 0));

        Assert.That(_board.TryGetRenderCameraFromBoard(2, out _, out var source, out _), Is.False);
        Assert.That(source, Is.EqualTo(DjiRenderPoseSource.None));
        Assert.That(Get("_propagationStartTimestampNs"), Is.EqualTo(0L));
    }

    [Test] public void BadVisualOrientation_IsNotRepairedOrSilentlyReplacedByNoMarkers()
    {
        _visual = new Pose(_visual.position, Quaternion.Euler(0, 180, 0)); SeedVisual(_visual, _baseline);
        AddTelemetry(); Process(Result(_baseline + Ms), true);
        _board.TryGetRenderCameraFromBoard(2, out var pose, out _, out _);
        AssertPose(pose, _visual); AssertBaseline();
    }

    [TestCase(false)] [TestCase(true)]
    public void RecordedOctober29NegativeFrames_ReproduceLegacyOrImmediateQualityLoss(bool enabled)
    {
        // The false case replays the offline baseline; normal ProcessResult is ON.
        var replay = JsonUtility.FromJson<Replay>(File.ReadAllText(Path.Combine(
            RequireReplayDirectory("DJI_NO_MARKERS_FIXTURE_DIR"), "oct29-fixture.json")));
        _visual = new Pose(replay.position, replay.rotation); _baseline = replay.baselineTimestampNs;
        SeedVisual(_visual, _baseline);
        Assert.That(replay.frames.Length, Is.EqualTo(16));
        foreach (var frame in replay.frames)
        {
            Call("ProcessResultWithQualityLossExperiment", Result(frame.sourceNs, frame.status, frame.markerCount, frame.sequence), frame.arrivalNs, enabled);
            Assert.That(_board.CurrentVisualPoseQuality, Is.EqualTo(enabled
                ? DjiBoardVisionProvider.VisualPoseQuality.Unavailable : DjiBoardVisionProvider.VisualPoseQuality.HighConfidence));
            AssertBaseline();
        }
    }

    [Test] public void DiagnosticsSerializeOrderingAndQuality_WithoutChangingState()
    {
        var json = DjiTelemetryValidationLogger.BuildNoMarkersQualityLossJson("fixture", new NoMarkersQualityLossDiagnostic(
            110, 100, 4, 7, -8, 0, 90, NoMarkersQualityLossDecision.Applied,
            DjiBoardVisionProvider.VisualPoseQuality.HighConfidence, DjiBoardVisionProvider.VisualPoseQuality.Unavailable, DjiRenderPoseSource.Visual));
        StringAssert.Contains("\"listenerGeneration\":4", json);
        StringAssert.Contains("\"sourceTimestampNs\":100", json);
        StringAssert.Contains("\"decision\":\"Applied\"", json);
        AssertBaseline();
    }

    [Test] public void RecordedGnssAndAttitude_ModelEarlyPropagationOnSavedApplicationSchedule()
    {
        var directory = RequireReplayDirectory("DJI_NO_MARKERS_FIXTURE_DIR");
        var replay = JsonUtility.FromJson<Replay>(File.ReadAllText(Path.Combine(directory, "oct29-fixture.json")));
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        var visual = new Pose(replay.position, replay.rotation);
        var positions = new List<TelemetryAircraftPositionSample>();
        foreach (var sample in replay.positions)
        {
            positions.Add(new TelemetryAircraftPositionSample(sample.timestampNs, sample.latitude, sample.longitude, sample.altitude));
            if (sample.timestampNs <= replay.baselineTimestampNs)
                coordinator.ObserveHorizontalGnssQuality(sample.satelliteCount, sample.timestampNs);
        }
        Assert.That(coordinator.TryInitializeFromVisualMeasurement(visual, replay.baselineTimestampNs, true, out var reason), Is.True, reason);
        Assert.That(coordinator.TryBeginRotationPropagation(visual, replay.baselineTimestampNs,
            replay.navigationFromCamera, positions, 300, out reason), Is.True, reason);
        var csv = new System.Text.StringBuilder("evaluationNs,attitudeNs,gnssNs,rawNorthM,rawEastM,acceptedNorthM,acceptedEastM,outputNorthM,outputEastM,reason\n");
        var evaluated = 0;
        foreach (var application in replay.applications)
        {
            if (application.timestampNs < replay.frames[0].arrivalNs) continue;
            foreach (var sample in replay.positions)
                if (sample.timestampNs <= application.timestampNs)
                    coordinator.ObserveHorizontalGnssQuality(sample.satelliteCount, sample.timestampNs);
            var prediction = ReferenceFrameTransforms.PredictCameraFromBoardRotationOnly(visual,
                replay.navigationFromCamera, application.navigationFromCamera);
            Assert.That(coordinator.TryEvaluateRotationPropagation(application.timestampNs, application.attitudeTimestampNs,
                application.navigationFromCamera, prediction, positions, 300, out reason), Is.True, reason);
            var estimate = coordinator.LatestHorizontalGnssSmoothingShadow.estimate;
            Assert.That(estimate.rawGnssTargetTimestampNs, Is.LessThanOrEqualTo(application.timestampNs));
            AssertPose(coordinator.Snapshot.worldFromBoard, Pose.identity);
            if (estimate.rawGnssTarget.magnitude < HorizontalGnssSmoothingEstimator.ProvisionalStationaryNoiseEnvelopeMeters)
                Assert.That(estimate.smoothedNavigationDelta.magnitude, Is.EqualTo(0), "Existing stationary HOLD remains active.");
            csv.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "{0},{1},{2},{3:R},{4:R},{5:R},{6:R},{7:R},{8:R},{9}\n",
                application.timestampNs, application.attitudeTimestampNs, estimate.rawGnssTargetTimestampNs,
                estimate.rawGnssTarget.x, estimate.rawGnssTarget.y, estimate.acceptedGnssTarget.x, estimate.acceptedGnssTarget.y,
                estimate.smoothedNavigationDelta.x, estimate.smoothedNavigationDelta.y, estimate.targetRejectedReason);
            evaluated++;
        }
        Assert.That(evaluated, Is.GreaterThan(10));
        File.WriteAllText(Path.Combine(directory, "modeled-early-propagation.csv"), csv.ToString());
    }

    [TestCase(false)] [TestCase(true)]
    public void EnabledOrDisabledDiagnosticWriter_DoesNotChangeQualityBaselineOrAppliedCamera(bool logging)
    {
        var loggerObject = new GameObject("No markers diagnostic logger");
        var logger = loggerObject.AddComponent<DjiTelemetryValidationLogger>();
        var output = new StringWriter();
        try
        {
            typeof(DjiTelemetryValidationLogger).GetField("_visualLatencyWriter", Private).SetValue(logger, output);
            typeof(DjiTelemetryValidationLogger).GetField("_telemetrySessionId", Private).SetValue(logger, "no-markers-test");
            typeof(DjiTelemetryValidationLogger).GetField("enableMotionDiagnostics", Private).SetValue(logger, logging);
            Set("_telemetryValidationLogger", logger);
            AddTelemetry(); Process(Result(_baseline + Ms), true);
            Assert.That(_board.CurrentVisualPoseQuality, Is.EqualTo(DjiBoardVisionProvider.VisualPoseQuality.Unavailable));
            Assert.That(_board.TryGetRenderCameraFromBoard(2, out var pose, out var source, out _), Is.True);
            Assert.That(source, Is.EqualTo(DjiRenderPoseSource.TelemetryPropagated));
            AssertPose(pose, _visual); AssertBaseline(); ApplyAndAssertCamera(pose);
            Assert.That(output.ToString().Contains("\"recordType\":\"NoMarkersQualityLoss\""), Is.EqualTo(logging));
        }
        finally
        {
            typeof(DjiTelemetryValidationLogger).GetField("_visualLatencyWriter", Private).SetValue(logger, null);
            Set("_telemetryValidationLogger", null); Object.DestroyImmediate(loggerObject); output.Dispose();
        }
    }

    [Test] public void DefaultExperimentOff_AndThresholdsUnchanged()
    {
        // Historical ID retained; the normal entry point now applies quality loss.
        Assert.That(DjiAprilTagNoMarkersQualityLossExperiment.Enabled, Is.True);
        Assert.That(Get("_validatedTrackingSafetyEnabled"), Is.True);
        Assert.That(Get("maxRotationOnlyPropagationSeconds"), Is.EqualTo(2f));
        Assert.That(Get("reacquisitionStableTimeSeconds"), Is.EqualTo(.25f));
        Assert.That(Get("reacquisitionMaximumRotationDifferenceDegrees"), Is.EqualTo(6f));
        Assert.That(Get("highConfidenceMinimumBorderDistancePixels"), Is.EqualTo(64f));
        Call("ProcessResult", Result(_baseline + Ms), _baseline + 2 * Ms);
        Assert.That(_board.CurrentVisualPoseQuality, Is.EqualTo(DjiBoardVisionProvider.VisualPoseQuality.Unavailable));
        AssertBaseline();
    }

    [Test]
    public void NormalProcessResult_MatchesExplicitQualityLossAndKeepsTheAcceptedBaseline()
    {
        AddTelemetry();
        Call("ProcessResult", Result(_baseline + Ms), _baseline + 2 * Ms);
        Assert.That(_board.CurrentVisualPoseQuality, Is.EqualTo(DjiBoardVisionProvider.VisualPoseQuality.Unavailable));
        Assert.That(_board.TryGetRenderCameraFromBoard(2, out var normalPose, out var normalSource, out _), Is.True);
        Assert.That(normalSource, Is.EqualTo(DjiRenderPoseSource.TelemetryPropagated));
        AssertPose(normalPose, _visual);
        AssertBaseline();
        SeedVisual(_visual, _baseline);
        Process(Result(_baseline + 3 * Ms, sequence: 2), true);
        Assert.That(_board.TryGetRenderCameraFromBoard(2, out var explicitPose, out var explicitSource, out _), Is.True);
        Assert.That(explicitSource, Is.EqualTo(normalSource));
        AssertPose(explicitPose, normalPose);
        AssertBaseline();
    }

    private static string RequireReplayDirectory(string variable)
    {
        var directory = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore($"Set {variable} to run with local replay data.");
        return directory;
    }

    private void SeedVisual(Pose pose, long timestamp)
    {
        Set("<CurrentRenderPoseSource>k__BackingField", DjiRenderPoseSource.Visual);
        Set("<CurrentVisualPoseQuality>k__BackingField", DjiBoardVisionProvider.VisualPoseQuality.HighConfidence);
        Set("<HasHighConfidenceCameraFromBoard>k__BackingField", true);
        Set("<CurrentHighConfidenceCameraFromBoard>k__BackingField", pose);
        Set("_currentHighConfidenceVisualTimestampNs", timestamp);
        Set("_lastHighConfidenceCameraFromBoardRealtime", Time.realtimeSinceStartup);
        Set("_hasShadowRotationPropagationBaseline", true);
        var type = typeof(DjiBoardVisionProvider).GetNestedType("ShadowRotationPropagationBaseline", BindingFlags.NonPublic);
        Set("_shadowRotationPropagationBaseline", Activator.CreateInstance(type, new object[] {
            timestamp, pose, Quaternion.identity, false, default(TelemetryAircraftPositionSample) }));
    }
    private void AddTelemetry(long timestamp = 0)
    {
        var telemetry = _root.AddComponent<DjiCameraPoseProvider>();
        typeof(DjiCameraPoseProvider).GetField("<HasTelemetryOrientation>k__BackingField", Private).SetValue(telemetry, true);
        typeof(DjiCameraPoseProvider).GetField("<LatestSnapshot>k__BackingField", Private).SetValue(telemetry,
            new DJIPoseSnapshot { telemetryArrivalTimestampNs = timestamp == 0 ? _baseline + Ms : timestamp });
        typeof(DjiCameraPoseProvider).GetField("_navigationFromCamera", Private).SetValue(telemetry, Matrix4x4.identity);
        Set("_telemetryProvider", telemetry);
        var history = (List<DjiCameraPoseProvider.TelemetryOrientationSample>)typeof(DjiCameraPoseProvider)
            .GetField("_telemetryOrientationHistory", Private).GetValue(telemetry);
        for (var t = 500; t <= 1600; t += 100)
            history.Add(new DjiCameraPoseProvider.TelemetryOrientationSample(t * Ms, Quaternion.identity, true, 0, 0));
    }
    private DjiBoardVisionProvider.NativeBoardResult Result(long timestamp, int status = -8, int count = 0, int sequence = 1) => new() {
        timestampNs = timestamp, visionFrameTimestampNs = timestamp, frameSequence = sequence, status = status, markerCount = count };
    private void Process(DjiBoardVisionProvider.NativeBoardResult result, bool enabled) =>
        Call("ProcessResultWithQualityLossExperiment", result, result.visionFrameTimestampNs + Ms, enabled);
    private void AssertBaseline()
    {
        Assert.That(Get("_currentHighConfidenceVisualTimestampNs"), Is.EqualTo(_baseline));
        AssertPose(_board.CurrentHighConfidenceCameraFromBoard, _visual);
        var baseline = Get("_shadowRotationPropagationBaseline");
        Assert.That(baseline.GetType().GetField("timestampNs").GetValue(baseline), Is.EqualTo(_baseline));
        AssertPose((Pose)baseline.GetType().GetField("cameraFromBoardVisual").GetValue(baseline), _visual);
    }
    private void ApplyAndAssertCamera(Pose pose)
    {
        typeof(DjiSharedReferenceCubeOverlay).GetMethod("ApplyStandaloneBoardOverlayPose", Private).Invoke(_overlay, new object[] { pose });
        var expected = DjiSharedReferenceCubeOverlay.NativeOpticalToUnityViewBasis * ReferenceFrameTransforms.MatrixFromPose(pose);
        for (var r = 0; r < 4; r++) for (var c = 0; c < 4; c++)
            Assert.That(_camera.worldToCameraMatrix[r, c], Is.EqualTo(expected[r, c]).Within(.0001f));
    }
    private static void AssertPose(Pose actual, Pose expected)
    {
        Assert.That(Vector3.Distance(actual.position, expected.position), Is.LessThan(.0001f));
        Assert.That(Quaternion.Angle(actual.rotation, expected.rotation), Is.LessThan(.05f));
    }
    private object Call(string method, params object[] args) => typeof(DjiBoardVisionProvider).GetMethod(method, Private).Invoke(_board, args);
    private object Get(string field) => typeof(DjiBoardVisionProvider).GetField(field, Private).GetValue(_board);
    private void Set(string field, object value) => typeof(DjiBoardVisionProvider).GetField(field, Private).SetValue(_board, value);
}
