using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

public sealed class VerticalRawAltitudeExperimentTests
{
    private const long Second = 1_000_000_000L;
    private static VerticalAltitudeSmoothingEstimator Create(float tau) => new(tau);

    [Test]
    public void ProductionDefault_MatchesPreviouslyOptedInRawTarget()
    {
        var normal = new VerticalAltitudeSmoothingEstimator();
        var historicalRaw = new VerticalAltitudeSmoothingEstimator(VerticalAltitudeSmoothingMode.RawTarget, .3f);
        Assert.That(normal.Mode, Is.EqualTo(VerticalAltitudeSmoothingMode.RawTarget));
        normal.Initialize(Second); historicalRaw.Initialize(Second);
        for (int i = 1; i <= 100; i++)
        {
            long t = Second + i * 17_000_000L;
            bool fresh = i % 4 != 0;
            long source = fresh ? t : 0L;
            var a = normal.Update(t, fresh, i * .01f, source);
            var b = historicalRaw.Update(t, fresh, i * .01f, source);
            Assert.That(a.state, Is.EqualTo(b.state));
            Assert.That(a.altitudeSourceTimestampNs, Is.EqualTo(b.altitudeSourceTimestampNs));
            Assert.That(a.sourceAccepted, Is.EqualTo(b.sourceAccepted));
            Assert.That(a.smoothedNavigationDown, Is.EqualTo(b.smoothedNavigationDown));
            Assert.That(a.alpha, Is.EqualTo(fresh ? 1f : 0f));
            Assert.That(a.tauSeconds, Is.Zero);
        }
    }

    [Test]
    public void NormalAndroidBuild_UsesRawTargetWithoutExperimentDefines()
    {
        var options = CommandLineAndroidBuild.CreateAndroidBuildOptions("unused.apk", BuildOptions.None);
        Assert.That(options.target, Is.EqualTo(BuildTarget.Android));
        Assert.That(options.extraScriptingDefines, Is.Empty);
        var projectDefines = PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.Android).Split(';');
        Assert.That(projectDefines.Where(d => d.StartsWith("DJI_", StringComparison.Ordinal)), Is.Empty);
        Assert.That(new VerticalAltitudeSmoothingEstimator().Mode, Is.EqualTo(VerticalAltitudeSmoothingMode.RawTarget));
    }

    [Test]
    public void DetectorCaptureBuild_OptsInOnlyToCapture()
    {
        var options = CommandLineAndroidBuild.CreateAndroidBuildOptions(
            "unused.apk", BuildOptions.None, detectorCaptureDiagnostic: true);
        Assert.That(options.extraScriptingDefines, Is.EqualTo(new[] { "DJI_EXACT_DETECTOR_CAPTURE" }));
    }

    [Test]
    public void ExplicitRawModeAndZeroTau_UseSafeBranchWithoutDivision()
    {
        foreach (var e in new[] { Create(0), new VerticalAltitudeSmoothingEstimator(VerticalAltitudeSmoothingMode.RawTarget) })
        {
            e.Initialize(Second);
            var result = e.Update(Second + 1, true, -1.25f, Second + 1);
            Assert.That(e.Mode, Is.EqualTo(VerticalAltitudeSmoothingMode.RawTarget));
            Assert.That(result.tauSeconds, Is.Zero);
            Assert.That(result.alpha, Is.EqualTo(1));
            Assert.That(result.smoothedNavigationDown, Is.EqualTo(-1.25f));
            Assert.That(float.IsFinite(result.smoothedNavigationDown), Is.True);
        }
    }

    [TestCase(float.NaN), TestCase(float.PositiveInfinity), TestCase(-1f)]
    public void InvalidTau_IsRejectedInsteadOfCreatingNonfiniteOutput(float tau) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(tau));

    [TestCase(.3f), TestCase(.1f), TestCase(0f)]
    public void ConstantAltitude_StaysAtBaseline(float tau)
    {
        var e = Create(tau); e.Initialize(Second);
        for (int i = 1; i < 100; i++)
            Assert.That(e.Update(Second + i * 20_000_000L, true, 0, Second + i * 20_000_000L).smoothedNavigationDown, Is.Zero);
    }

    [TestCase(.3f, -1f), TestCase(.3f, 1f), TestCase(.1f, -1f), TestCase(.1f, 1f), TestCase(0f, -1f), TestCase(0f, 1f)]
    public void GradualAscentAndDescent_RemainCausalWithoutOvershoot(float tau, float direction)
    {
        var e = Create(tau); e.Initialize(Second); float previous = 0;
        for (int i = 1; i <= 100; i++)
        {
            long t = Second + i * 20_000_000L; float target = direction * i * .01f;
            var r = e.Update(t, true, target, t);
            Assert.That(direction * r.smoothedNavigationDown, Is.InRange(direction * previous, direction * target));
            if (tau == 0) Assert.That(r.smoothedNavigationDown, Is.EqualTo(target));
            previous = r.smoothedNavigationDown;
        }
    }

    [TestCase(.3f), TestCase(.1f), TestCase(0f)]
    public void StepAndNonuniformEvaluationIntervals_MatchExactRule(float tau)
    {
        var e = Create(tau); e.Initialize(Second); long time = Second; double expected = 0;
        foreach (int ms in new[] { 10, 33, 7, 50, 21, 100, 16, 200 })
        {
            time += ms * 1_000_000L;
            expected += (-1 - expected) * (tau == 0 ? 1 : 1 - Math.Exp(-ms / 1000d / tau));
            var r = e.Update(time, true, -1, time);
            Assert.That(r.smoothedNavigationDown, Is.EqualTo(expected).Within(1e-6));
            Assert.That(float.IsFinite(r.smoothedNavigationDown), Is.True);
        }
    }

    [TestCase(.3f), TestCase(.1f), TestCase(0f)]
    public void NoiseAndFiveHzSamples_AreHeldWithoutFutureInterpolation(float tau)
    {
        var e = Create(tau); e.Initialize(Second); float target = 0, previous = 0; long source = Second;
        var values = new[] { .1f, -.1f, .05f, 0f, -.05f };
        for (int i = 1; i <= 500; i++)
        {
            long t = Second + i * 20_000_000L;
            if ((i - 1) % 10 == 0) { target = values[((i - 1) / 10) % values.Length]; source = t; }
            var r = e.Update(t, true, target, source);
            Assert.That(r.smoothedNavigationDown, Is.InRange(-.1f, .1f));
            if (tau == 0)
            {
                Assert.That(r.smoothedNavigationDown, Is.EqualTo(target));
                if ((i - 1) % 10 != 0) Assert.That(r.smoothedNavigationDown, Is.EqualTo(previous));
            }
            previous = r.smoothedNavigationDown;
        }
    }

    [TestCase(.3f), TestCase(.1f), TestCase(0f)]
    public void StaleSource_AdvancesClockAndFreezes_ThenRecoversFromHeldValue(float tau)
    {
        var e = Create(tau); e.Initialize(Second);
        var first = e.Update(Second + 100_000_000L, true, -1, Second + 100_000_000L);
        for (int i = 2; i <= 100; i++)
        {
            var stale = e.Update(Second + i * 100_000_000L, false, 999, 0);
            Assert.That(stale.smoothedNavigationDown, Is.EqualTo(first.smoothedNavigationDown));
            Assert.That(stale.state, Is.EqualTo(VerticalAltitudeLocalizationState.DegradedStale));
            Assert.That(stale.desiredCorrection, Is.Zero);
        }
        var resumed = e.Update(Second + 10_020_000_000L, true, -2, Second + 10_020_000_000L);
        double alpha = tau == 0 ? 1 : 1 - Math.Exp(-.02 / tau);
        Assert.That(resumed.smoothedNavigationDown, Is.EqualTo(first.smoothedNavigationDown + (-2 - first.smoothedNavigationDown) * alpha).Within(1e-6));
    }

    [TestCase(299, true), TestCase(300, true), TestCase(301, false)]
    public void FreshnessContract_RemainsThreeHundredMilliseconds(int ageMs, bool expected)
    {
        bool fresh = DjiAircraftPositionFreshness.IsFreshAtOrBefore(Second, Second + ageMs * 1_000_000L,
            VerticalAltitudeSmoothingEstimator.DefaultMaximumAgeMilliseconds, out _);
        Assert.That(fresh, Is.EqualTo(expected));
        var e = new VerticalAltitudeSmoothingEstimator(); e.Initialize(Second);
        var r = e.Update(Second + ageMs * 1_000_000L, fresh, -1, fresh ? Second : 0);
        Assert.That(r.smoothedNavigationDown, Is.EqualTo(expected ? -1 : 0));
    }

    [TestCase(.3f), TestCase(.1f), TestCase(0f)]
    public void FirstEvaluationAndFirstValidSample_RespectBaselineAndMissingInput(float tau)
    {
        var e = Create(tau);
        Assert.That(e.Update(Second, true, -1, Second).hasEstimate, Is.False);
        Assert.That(e.Initialize(0), Is.False); Assert.That(e.Initialize(Second), Is.True);
        Assert.That(e.Update(Second, true, 0, Second).smoothedNavigationDown, Is.Zero);
        var held = e.Update(Second + 10 * Second, false, -3.2f, 0);
        Assert.That(held.smoothedNavigationDown, Is.Zero);
        var fresh = e.Update(Second + 10 * Second + 20_000_000L, true, -3.2f, Second + 10 * Second + 20_000_000L);
        double alpha = tau == 0 ? 1 : 1 - Math.Exp(-.02 / tau);
        Assert.That(fresh.smoothedNavigationDown, Is.EqualTo(-3.2 * alpha).Within(1e-6));
    }

    [TestCase(.3f), TestCase(.1f), TestCase(0f)]
    public void DuplicateAndOutOfOrderInputs_DoNotReingestOrExtrapolate(float tau)
    {
        var e = Create(tau); e.Initialize(Second);
        var first = e.Update(Second + 100_000_000L, true, -1, Second + 100_000_000L);
        var same = e.Update(Second + 100_000_000L, true, 999, Second + 100_000_000L);
        Assert.That(same.smoothedNavigationDown, Is.EqualTo(first.smoothedNavigationDown));
        Assert.That(same.sourceAccepted, Is.False); Assert.That(same.desiredCorrection, Is.Zero);
        Assert.That(e.Update(Second, true, 999, Second).hasEstimate, Is.False);
        Assert.That(e.Update(Second + 200_000_000L, true, 999, Second + 300_000_000L).hasEstimate, Is.False);
        var recovery = e.Update(Second + 300_000_000L, true, -2, Second + 300_000_000L);
        Assert.That(float.IsFinite(recovery.smoothedNavigationDown), Is.True);
    }

    [TestCase(.3f), TestCase(.1f), TestCase(0f)]
    public void VisualLossReacquisitionAndRepeatedOutages_ResetOnlyOutageState(float tau)
    {
        var c = Coordinator(tau); var positions = new List<TelemetryAircraftPositionSample> { new(Second, 47, 19, 100) };
        Assert.That(c.TryInitializeFromVisualMeasurement(Pose.identity, Second, true, out var reason), Is.True, reason);
        var board = c.Snapshot.worldFromBoard; var visual = Pose.identity; long baseline = Second;
        for (int cycle = 0; cycle < 3; cycle++)
        {
            positions.Add(new(baseline, 47, 19, 100 + cycle));
            Assert.That(c.TryBeginRotationPropagation(visual, baseline, Quaternion.identity, positions, 300, out reason), Is.True, reason);
            Assert.That(c.ProductionVerticalAltitude.smoothedDeltaNavigationDown, Is.Zero);
            long t = baseline + 100_000_000L; positions.Add(new(t, 47, 19, 100 + cycle + .2));
            Assert.That(c.TryEvaluateRotationPropagation(t, t, Quaternion.identity, visual, positions, 300, out reason), Is.True, reason);
            Assert.That(c.Snapshot.phase, Is.EqualTo(WorldLocalizationPhase.Propagating));
            var accepted = new Pose(new Vector3(cycle * .1f, .2f, .3f), Quaternion.Euler(2, cycle, 1));
            long acceptedAt = t + 100_000_000L; positions.Add(new(acceptedAt, 47, 19, 101 + cycle));
            Assert.That(c.TryUpdateFromVisualMeasurement(accepted, acceptedAt, false, positions, 300, out reason), Is.False);
            Assert.That(c.TryUpdateFromVisualMeasurement(accepted, acceptedAt, true, positions, 300, out reason), Is.True, reason);
            Assert.That(c.Snapshot.phase, Is.EqualTo(WorldLocalizationPhase.VisualLocalized));
            Assert.That(c.Snapshot.worldFromBoard, Is.EqualTo(board));
            Assert.That(c.ProductionVerticalAltitude.hasContribution, Is.False);
            var expected = ReferenceFrameTransforms.Invert(accepted);
            Assert.That(Vector3.Distance(c.Snapshot.worldFromCamera.position, expected.position), Is.LessThan(1e-5));
            Assert.That(Quaternion.Angle(c.Snapshot.worldFromCamera.rotation, expected.rotation), Is.LessThan(.001));
            visual = accepted; baseline = acceptedAt;
        }
    }

    [Test]
    public void RawVersusBaseline_LeavesNavigationHorizontalAndOrientationIdentical()
    {
        var baseline = Coordinator(.3f); var raw = new DjiWorldLocalizationShadowCoordinator();
        var positions = new List<TelemetryAircraftPositionSample> { new(Second, 47, 19, 100) };
        var visual = new Pose(new Vector3(.2f, -.1f, 2), Quaternion.Euler(4, 12, 3));
        var nav = Quaternion.Euler(20, 30, 5);
        var worldNavigation = ReferenceFrameTransforms.Invert(visual).rotation * Quaternion.Inverse(nav);
        foreach (var c in new[] { baseline, raw })
        {
            Assert.That(c.TryInitializeFromVisualMeasurement(visual, Second, true, out var reason), Is.True, reason);
            Assert.That(c.TryBeginRotationPropagation(visual, Second, nav, positions, 300, out reason), Is.True, reason);
        }
        for (int i = 1; i <= 50; i++)
        {
            long t = Second + i * 100_000_000L; positions.Add(new(t, 47 + i * .0000002, 19 + i * .0000001, 100 + i * .02));
            foreach (var c in new[] { baseline, raw })
                Assert.That(c.TryEvaluateRotationPropagation(t, t, Quaternion.Euler(20, 30 + i * .1f, 5), visual, positions, 300, out var reason), Is.True, reason);
            Assert.That(raw.LatestHorizontalGnssSmoothingShadow.navigationDelta, Is.EqualTo(baseline.LatestHorizontalGnssSmoothingShadow.navigationDelta));
            var difference = Quaternion.Inverse(worldNavigation) * (raw.Snapshot.worldFromCamera.position - baseline.Snapshot.worldFromCamera.position);
            Assert.That(Mathf.Abs(difference.x), Is.LessThan(1e-5)); Assert.That(Mathf.Abs(difference.y), Is.LessThan(1e-5));
            Assert.That(raw.Snapshot.worldFromCamera.rotation, Is.EqualTo(baseline.Snapshot.worldFromCamera.rotation));
            Assert.That(raw.Snapshot.worldFromBoard, Is.EqualTo(baseline.Snapshot.worldFromBoard));
        }
    }

    [TestCase(.3f), TestCase(.1f), TestCase(0f)]
    public void RecordedCausalReplay_MatchesEstimatorForBothSessions(float tau)
    {
        var directory = Environment.GetEnvironmentVariable("DJI_VERTICAL_RAW_REPLAY_DIR");
        if (string.IsNullOrEmpty(directory))
        {
            var portable = ReplayEstimator(tau); portable.Initialize(Second); double expected = 0;
            for (int i = 1; i <= 1500; i++)
            {
                long t = Second + i * 20_000_000L; float target = (float)Math.Sin(i * .01);
                expected = tau == 0 ? target : expected + (target - expected) * (1 - Math.Exp(-.02 / tau));
                Assert.That(portable.Update(t, true, target, t).smoothedNavigationDown, Is.EqualTo(expected).Within(2e-5));
            }
            return;
        }
        string key = null; VerticalAltitudeSmoothingEstimator estimator = null; int count = 0;
        var sessions = new HashSet<string>();
        foreach (string line in File.ReadLines(Path.Combine(directory, "vertical-raw-replay-fixture.csv")).Skip(1))
        {
            var f = line.Split(','); if (Math.Abs(float.Parse(f[6], CultureInfo.InvariantCulture) - tau) > .00001f) continue;
            string next = f[0] + "/" + f[1]; if (key != next) { key = next; estimator = ReplayEstimator(tau); estimator.Initialize(long.Parse(f[1])); }
            long evaluation = long.Parse(f[2]), source = long.Parse(f[3]); Assert.That(source, Is.LessThanOrEqualTo(evaluation));
            var result = estimator.Update(evaluation, f[4] == "1", float.Parse(f[5], CultureInfo.InvariantCulture), source);
            Assert.That(result.smoothedNavigationDown, Is.EqualTo(double.Parse(f[7], CultureInfo.InvariantCulture)).Within(2e-5));
            Assert.That(float.IsFinite(result.smoothedNavigationDown), Is.True); count++; sessions.Add(f[0]);
        }
        Assert.That(count, Is.GreaterThan(1000)); Assert.That(sessions.Count, Is.EqualTo(2));
        TestContext.WriteLine("Causal replay rows checked=" + count);
    }

    private static VerticalAltitudeSmoothingEstimator ReplayEstimator(float tau) =>
        tau == 0f ? new VerticalAltitudeSmoothingEstimator() : Create(tau);

    private static DjiWorldLocalizationShadowCoordinator Coordinator(float tau)
    {
        var c = new DjiWorldLocalizationShadowCoordinator();
        typeof(DjiWorldLocalizationShadowCoordinator).GetField("_verticalAltitudeSmoothingEstimator", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(c, Create(tau));
        return c;
    }

}
