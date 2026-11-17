using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

public sealed class HorizontalAdmissionTraceTests
{
    private const long Ms = 1_000_000L;

    [Test]
    public void AdmissionJson_PreservesPreviousTargetRawDistanceAndConfirmedCount()
    {
        var estimator = NewEstimator();
        var before = estimator.Update(200 * Ms, true, Vector2.zero, 200 * Ms);
        before = estimator.Update(300 * Ms, true, new Vector2(0.09f, 0f), 300 * Ms);
        var after = estimator.Update(400 * Ms, true, new Vector2(0.12f, 0f), 400 * Ms);
        var row = JsonUtility.FromJson<Row>(DjiTelemetryValidationLogger.BuildHorizontalAdmissionTraceJson("s", Trace(before, after)));
        Assert.That(row.recordType, Is.EqualTo("HorizontalAdmissionTrace"));
        Assert.That(row.monotonicTime, Is.EqualTo(400 * Ms));
        Assert.That(row.gnssMeasurementTime, Is.EqualTo(400 * Ms));
        Assert.That(row.previousAcceptedTarget.northM, Is.EqualTo(0f));
        Assert.That(row.candidateDisplacementMagnitude, Is.EqualTo(0.12f).Within(0.0001f));
        Assert.That(row.directionConsistencyCountBefore, Is.EqualTo(1));
        Assert.That(row.directionConsistencyCount, Is.EqualTo(2));
        Assert.That(row.directionConsistencyCountAfter, Is.EqualTo(0));
        Assert.That(row.admitted, Is.True);
        Assert.That(row.acceptedTarget.northM, Is.EqualTo(0.12f));
        Assert.That(row.smootherOutput.northM, Is.GreaterThan(0f));
    }

    [Test]
    public void Gate_SkipsRenderReplaysButPreservesNewerIdenticalValueCallbacks()
    {
        var estimator = NewEstimator();
        var a = estimator.Update(200 * Ms, true, Vector2.zero, 200 * Ms);
        var b = estimator.Update(220 * Ms, true, Vector2.zero, 200 * Ms);
        var gate = new HorizontalAdmissionTraceGate();
        Assert.That(gate.ShouldWrite(Trace(a, b)), Is.True);
        Assert.That(gate.ShouldWrite(Trace(a, b)), Is.False);
        var replay = estimator.Update(240 * Ms, true, Vector2.zero, 200 * Ms);
        Assert.That(gate.ShouldWrite(Trace(b, replay)), Is.False);
        var c = estimator.Update(300 * Ms, true, Vector2.zero, 300 * Ms);
        Assert.That(gate.ShouldWrite(Trace(replay, c)), Is.True);
    }

    [Test]
    public void Gate_LogsStaleAndQualityTransitionsWithoutNewMeasurements()
    {
        var estimator = NewEstimator();
        var a = estimator.Update(200 * Ms, true, Vector2.zero, 200 * Ms);
        var b = estimator.Update(600 * Ms, false, Vector2.zero, 0L);
        var c = estimator.FreezeForGnssQuality(620 * Ms, "LOW_QUALITY");
        var gate = new HorizontalAdmissionTraceGate();
        Assert.That(gate.ShouldWrite(Trace(default, a)), Is.True);
        Assert.That(gate.ShouldWrite(Trace(a, b, false)), Is.True);
        Assert.That(gate.ShouldWrite(Trace(b, c, false, false)), Is.True);
    }

    [Test]
    public void LoggingDisabledAndEnabled_ProduceIdenticalEstimatorOutputsAcrossResume()
    {
        var enabled = NewEstimator();
        var disabled = NewEstimator();
        var gate = new HorizontalAdmissionTraceGate();
        var previous = default(HorizontalGnssSmoothingEstimate);
        var records = 0;
        for (var i = 1; i <= 600; i++)
        {
            var timestamp = (100 + i * 20) * Ms;
            var source = (100 + (i / 10) * 200) * Ms;
            var target = new Vector2(i < 100 ? 0f : (i - 100) * 0.002f, 0.1f);
            var fresh = i < 200 || i > 450;
            var a = enabled.Update(timestamp, fresh, target, source);
            if (gate.ShouldWrite(Trace(previous, a, fresh)))
            {
                var json = DjiTelemetryValidationLogger.BuildHorizontalAdmissionTraceJson("test", Trace(previous, a, fresh));
                Assert.That(json, Does.Not.Contain("NaN"));
                records++;
            }
            var b = disabled.Update(timestamp, fresh, target, source);
            Assert.That(a.smoothedNavigationDelta, Is.EqualTo(b.smoothedNavigationDelta));
            Assert.That(a.acceptedGnssTarget, Is.EqualTo(b.acceptedGnssTarget));
            Assert.That(a.acceptedGnssTargetTimestampNs, Is.EqualTo(b.acceptedGnssTargetTimestampNs));
            Assert.That(a.targetAccepted, Is.EqualTo(b.targetAccepted));
            Assert.That(a.temporalConsistencyCount, Is.EqualTo(b.temporalConsistencyCount));
            Assert.That(a.appliedCorrection, Is.EqualTo(b.appliedCorrection));
            Assert.That(a.state, Is.EqualTo(b.state));
            previous = a;
        }
        Assert.That(records, Is.GreaterThan(10));
    }

    [Test]
    public void InvalidRawSource_IsExplicitlyUnavailableRatherThanZeroMovement()
    {
        var estimator = NewEstimator();
        var before = estimator.Update(200 * Ms, true, Vector2.zero, 200 * Ms);
        var after = estimator.Update(600 * Ms, false, Vector2.zero, 0L);
        var json = DjiTelemetryValidationLogger.BuildHorizontalAdmissionTraceJson("s", Trace(before, after, false));
        Assert.That(json, Does.Contain("\"gnssFresh\":false"));
        Assert.That(json, Does.Contain("\"rawHorizontalNedPosition\":{\"northM\":null,\"eastM\":null}"));
        Assert.That(json, Does.Contain("GNSS_TARGET_STALE"));
    }

    [Test]
    public void AppliedTrace_ContainsEvaluationAdmissionAndActualApplicationTimes()
    {
        var estimator = NewEstimator();
        var before = estimator.Update(200 * Ms, true, Vector2.zero, 200 * Ms);
        var after = estimator.Update(300 * Ms, true, Vector2.one, 300 * Ms);
        var json = DjiTelemetryValidationLogger.BuildHorizontalAdmissionTraceJson("s", Trace(before, after),
            "FirstOutputApplied", 350 * Ms, new Pose(Vector3.one, Quaternion.identity), 300 * Ms);
        Assert.That(json, Does.Contain("\"applicationTimestampNs\":350000000"));
        Assert.That(json, Does.Contain("\"admissionEvaluationTimestampNs\":300000000"));
        Assert.That(json, Does.Contain("appliedCameraFromBoardPosition"));
    }

    [Test]
    public void DisabledMotionDiagnostics_WritesNoDetailedTransitions()
    {
        var go = new GameObject("Disabled Motion Logger Test");
        try
        {
            var logger = go.AddComponent<DjiTelemetryValidationLogger>();
            using var memory = new MemoryStream();
            using var writer = new StreamWriter(memory);
            Set(logger, "_visualLatencyWriter", writer);
            Set(logger, "enableMotionDiagnostics", false);
            logger.RecordMotionRenderSourceTransition(100L, DjiRenderPoseSource.Visual, DjiRenderPoseSource.TelemetryPropagated);
            writer.Flush();
            Assert.That(memory.Length, Is.Zero);
            Set(logger, "_visualLatencyWriter", null);
        }
        finally { Object.DestroyImmediate(go); }
    }

    [Test]
    public void SourceLifecycleSerialization_UsesSameSessionAndMonotonicDomain()
    {
        foreach (var type in new[] { "AuthoritativeVisualPoseAccepted", "VisualReacquisition", "PropagationStarted", "PropagationStopped" })
        {
            var json = DjiTelemetryValidationLogger.BuildMotionEventJson(type, "session", 123456L, "Visual", "TelemetryPropagated");
            var row = JsonUtility.FromJson<Row>(json);
            Assert.That(row.recordType, Is.EqualTo(type));
            Assert.That(row.monotonicTime, Is.EqualTo(123456L));
            Assert.That(json, Does.Contain("\"telemetrySessionId\":\"session\""));
            Assert.That(json, Does.Contain("ANDROID_MONOTONIC"));
        }
    }

    private static HorizontalGnssSmoothingEstimator NewEstimator()
    {
        var estimator = new HorizontalGnssSmoothingEstimator();
        estimator.Initialize(100 * Ms);
        return estimator;
    }

    private static HorizontalAdmissionTrace Trace(HorizontalGnssSmoothingEstimate before,
        HorizontalGnssSmoothingEstimate after, bool fresh = true, bool trusted = true)
    {
        var delta = new Vector3(after.rawGnssTarget.x, after.rawGnssTarget.y, 0f);
        var raw = fresh ? HorizontalGnssWorldShadowSnapshot.Valid(100 * Ms, 100 * Ms, 0d,
            after.targetTimestampNs, after.rawGnssTargetTimestampNs, 0d, delta, delta, Pose.identity, Quaternion.identity) :
            HorizontalGnssWorldShadowSnapshot.Invalid("GNSS_TARGET_STALE", 100 * Ms, targetTimestampNs: after.targetTimestampNs);
        var current = HorizontalGnssSmoothingShadowSnapshot.FromEstimate(100 * Ms, after.targetTimestampNs, raw, after,
            Vector3.zero, Vector3.zero, Pose.identity, Quaternion.identity, true, 0f);
        var quality = new HorizontalGnssQualityAdmissionSnapshot(true, 25, trusted,
            HorizontalGnssQualityTransition.None, 0, trusted ? "TRUSTED" : "LOW_QUALITY", after.targetTimestampNs);
        return new HorizontalAdmissionTrace(after.targetTimestampNs, before, current, quality, 100 * Ms, 0d, default);
    }

    private static void Set(object target, string field, object value) => target.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);

    [Serializable] private sealed class Vec { public float northM; public float eastM; }
    [Serializable] private sealed class Row
    {
        public string recordType;
        public long monotonicTime;
        public long gnssMeasurementTime;
        public Vec previousAcceptedTarget;
        public Vec acceptedTarget;
        public Vec smootherOutput;
        public float candidateDisplacementMagnitude;
        public int directionConsistencyCountBefore;
        public int directionConsistencyCount;
        public int directionConsistencyCountAfter;
        public bool admitted;
    }
}
