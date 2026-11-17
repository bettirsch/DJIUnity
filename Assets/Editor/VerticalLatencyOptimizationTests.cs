using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public sealed class VerticalLatencyOptimizationTests
{
    private const long Second = 1_000_000_000L;

    [TestCase(.4f), TestCase(.3f)]
    public void ConstantAltitude_RemainsExactlyHeld(float tau)
    {
        var e = new VerticalAltitudeSmoothingEstimator(tau); e.Initialize(Second);
        for (int i = 1; i <= 100; i++) Assert.That(e.Update(Second+i*20_000_000L,true,0,Second+i*20_000_000L).smoothedNavigationDown,Is.Zero);
    }

    [TestCase(-1f), TestCase(1f)]
    public void GradualAscentAndDescent_RespondFasterWithoutCrossingTarget(float direction)
    {
        var old = new VerticalAltitudeSmoothingEstimator(.4f); var candidate = new VerticalAltitudeSmoothingEstimator(.3f);
        old.Initialize(Second); candidate.Initialize(Second); float previous=0;
        for(int i=1;i<=100;i++) {
            var t=Second+i*20_000_000L;float target=direction*i*.01f;
            var a=old.Update(t,true,target,t);var b=candidate.Update(t,true,target,t);
            Assert.That(Mathf.Abs(target-b.smoothedNavigationDown),Is.LessThanOrEqualTo(Mathf.Abs(target-a.smoothedNavigationDown)+1e-6f));
            Assert.That(direction*b.smoothedNavigationDown,Is.InRange(direction*previous,direction*target));
            previous=b.smoothedNavigationDown;
        }
    }

    [TestCase(.4f), TestCase(.3f), TestCase(.25f), TestCase(.2f)]
    public void SuddenStep_UsesExactTauAndNeverOvershoots(float tau)
    {
        var e=new VerticalAltitudeSmoothingEstimator(tau);e.Initialize(Second);
        for(int i=1;i<=100;i++) {
            var t=Second+i*20_000_000L;var r=e.Update(t,true,-1,t);
            Assert.That(r.smoothedNavigationDown,Is.EqualTo(-(1-Math.Exp(-i*.02/tau))).Within(2e-6));
            Assert.That(r.smoothedNavigationDown,Is.InRange(-1f,0f));
        }
    }

    [TestCase(.4f), TestCase(.3f)]
    public void MissingAndStaleAltitude_FreezeAndResumeWithoutCatchup(float tau)
    {
        var e=new VerticalAltitudeSmoothingEstimator(tau);e.Initialize(Second);
        var initial=e.Update(Second+100_000_000L,true,-.5f,Second+100_000_000L);
        for(int i=2;i<=200;i++) {
            var stale=e.Update(Second+i*100_000_000L,false,999,0);
            Assert.That(stale.smoothedNavigationDown,Is.EqualTo(initial.smoothedNavigationDown));
            Assert.That(stale.state,Is.EqualTo(VerticalAltitudeLocalizationState.DegradedStale));
        }
        var resumed=e.Update(Second+20_020_000_000L,true,-1,Second+20_020_000_000L);
        Assert.That(resumed.smoothedNavigationDown-initial.smoothedNavigationDown,
            Is.EqualTo((-1-initial.smoothedNavigationDown)*(1-Math.Exp(-.02/tau))).Within(1e-6));
    }

    [Test]
    public void IdenticalEvaluationTimestamp_DoesNotDoubleApplyCorrection()
    {
        var e=new VerticalAltitudeSmoothingEstimator();e.Initialize(Second);
        var a=e.Update(Second+100_000_000L,true,-1,Second+100_000_000L);
        var b=e.Update(Second+100_000_000L,true,-1,Second+100_000_000L);
        Assert.That(b.smoothedNavigationDown,Is.EqualTo(a.smoothedNavigationDown));
        Assert.That(b.desiredCorrection,Is.Zero);
    }

    [Test]
    public void StationarySyntheticNoise_IsBoundedAndQuantifiesNoiseTradeoff()
    {
        var old=new VerticalAltitudeSmoothingEstimator(.4f);var candidate=new VerticalAltitudeSmoothingEstimator(.3f);
        old.Initialize(Second);candidate.Initialize(Second);var random=new System.Random(80032);
        var a=new List<float>();var b=new List<float>();float target=0;long source=Second;
        for(int i=1;i<=20000;i++) {
            var t=Second+i*20_000_000L;
            if((i-1)%10==0){target=(float)(random.NextDouble()-.5)*.1f;source=t;}
            var x=old.Update(t,true,target,source).smoothedNavigationDown;var y=candidate.Update(t,true,target,source).smoothedNavigationDown;
            Assert.That(Mathf.Abs(y),Is.LessThanOrEqualTo(.05f));
            if(i>1000){a.Add(x);b.Add(y);}
        }
        double Sd(List<float> values){var mean=values.Average(v=>(double)v);return Math.Sqrt(values.Average(v=>(v-mean)*(v-mean)));}
        var ratio=Sd(b)/Sd(a);
        Assert.That(ratio,Is.InRange(1d,1.25d),"Synthetic noise tradeoff, not a production quality threshold");
        TestContext.WriteLine($"Synthetic output std old={Sd(a):R} new={Sd(b):R} ratio={ratio:R}");
    }

    [Test]
    public void VisualLossAndReacquisition_PreserveWorldBoardAndResetOutageBaseline()
    {
        var c=new DjiWorldLocalizationShadowCoordinator();var samples=new List<TelemetryAircraftPositionSample>{new(Second,47,19,100),new(Second+100_000_000L,47,19,101)};
        Assert.That(c.TryInitializeFromVisualMeasurement(Pose.identity,Second,true,out var reason),Is.True,reason);
        var board=c.Snapshot.worldFromBoard;
        Assert.That(c.TryBeginRotationPropagation(Pose.identity,Second,Quaternion.identity,samples,300,out reason),Is.True,reason);
        Assert.That(c.TryUpdateRotationPropagation(Second+100_000_000L,Quaternion.identity,Pose.identity,samples,300,out reason),Is.True,reason);
        Assert.That(c.Snapshot.phase,Is.EqualTo(WorldLocalizationPhase.Propagating));
        var visual=new Pose(new Vector3(0,0,2),Quaternion.Euler(2,20,1));
        Assert.That(c.TryUpdateFromVisualMeasurement(visual,Second+200_000_000L,true,samples,300,out reason),Is.True,reason);
        Assert.That(c.Snapshot.phase,Is.EqualTo(WorldLocalizationPhase.VisualLocalized));
        Assert.That(c.Snapshot.worldFromBoard,Is.EqualTo(board));
        Assert.That(c.ProductionVerticalAltitude.hasContribution,Is.False);
        Assert.That(c.TryBeginRotationPropagation(visual,Second+200_000_000L,Quaternion.identity,samples,300,out reason),Is.True,reason);
        Assert.That(c.ProductionVerticalAltitude.smoothedDeltaNavigationDown,Is.Zero);
    }

    [Test]
    public void ChangingOnlyVerticalTau_LeavesHorizontalRotationAndWorldBoardIdentical()
    {
        var old=new DjiWorldLocalizationShadowCoordinator();var candidate=new DjiWorldLocalizationShadowCoordinator();
        typeof(DjiWorldLocalizationShadowCoordinator).GetField("_verticalAltitudeSmoothingEstimator",BindingFlags.Instance|BindingFlags.NonPublic)
            .SetValue(old,new VerticalAltitudeSmoothingEstimator(.4f));
        typeof(DjiWorldLocalizationShadowCoordinator).GetField("_verticalAltitudeSmoothingEstimator",BindingFlags.Instance|BindingFlags.NonPublic)
            .SetValue(candidate,new VerticalAltitudeSmoothingEstimator(.3f));
        var samples=new List<TelemetryAircraftPositionSample>{new(Second,47,19,100)};
        foreach(var c in new[]{old,candidate}) {
            Assert.That(c.TryInitializeFromVisualMeasurement(Pose.identity,Second,true,out var reason),Is.True,reason);
            Assert.That(c.TryBeginRotationPropagation(Pose.identity,Second,Quaternion.identity,samples,300,out reason),Is.True,reason);
        }
        for(int i=1;i<=50;i++) {
            var t=Second+i*100_000_000L;samples.Add(new(t,47+i*.0000002,19+i*.0000001,100+i*.02));
            foreach(var c in new[]{old,candidate})Assert.That(c.TryEvaluateRotationPropagation(t,t,Quaternion.Euler(0,i*.1f,0),Pose.identity,samples,300,out var reason),Is.True,reason);
            Assert.That(candidate.LatestHorizontalGnssSmoothingShadow.navigationDelta,Is.EqualTo(old.LatestHorizontalGnssSmoothingShadow.navigationDelta));
            Assert.That(candidate.Snapshot.worldFromCamera.rotation,Is.EqualTo(old.Snapshot.worldFromCamera.rotation));
            Assert.That(candidate.Snapshot.worldFromBoard,Is.EqualTo(old.Snapshot.worldFromBoard));
            Assert.That(candidate.Snapshot.worldFromCamera.position.x,Is.EqualTo(old.Snapshot.worldFromCamera.position.x).Within(1e-6f));
            Assert.That(candidate.Snapshot.worldFromCamera.position.y,Is.EqualTo(old.Snapshot.worldFromCamera.position.y).Within(1e-6f));
        }
    }

    [TestCase(.4f),TestCase(.3f),TestCase(.25f),TestCase(.2f)]
    public void ProductionEstimator_MatchesIndependentCausalReference(float tau)
    {
        var directory=Environment.GetEnvironmentVariable("DJI_VERTICAL_REPLAY_DIR");
        if(!string.IsNullOrEmpty(directory)) {
            var path=Path.Combine(directory,"vertical-replay-fixture.csv");Assert.That(File.Exists(path),Is.True);
            string key=null;VerticalAltitudeSmoothingEstimator estimator=null;int checkedRows=0;
            foreach(var line in File.ReadLines(path).Skip(1)) {
                var f=line.Split(',');if(Math.Abs(float.Parse(f[6],CultureInfo.InvariantCulture)-tau)>.00001f)continue;
                if(key!=f[0]){key=f[0];estimator=new VerticalAltitudeSmoothingEstimator(tau);estimator.Initialize(long.Parse(f[0],CultureInfo.InvariantCulture));}
                var evaluation=long.Parse(f[1],CultureInfo.InvariantCulture);var source=long.Parse(f[2],CultureInfo.InvariantCulture);
                Assert.That(source,Is.LessThanOrEqualTo(evaluation));
                var result=estimator.Update(evaluation,f[3]=="1",float.Parse(f[4],CultureInfo.InvariantCulture),source);
                Assert.That(result.smoothedNavigationDown,Is.EqualTo(double.Parse(f[7],CultureInfo.InvariantCulture)).Within(2e-5));checkedRows++;
            }
            Assert.That(checkedRows,Is.GreaterThan(1000));TestContext.WriteLine("Recorded causal replay rows="+checkedRows);return;
        }
        // Portable double-precision reference when the external flight archive
        // is not supplied; no session files are required for ordinary CI.
        var e=new VerticalAltitudeSmoothingEstimator(tau);e.Initialize(Second);double expected=0;
        for(int i=1;i<1000;i++){var target=(float)Math.Sin(i*.01);expected+=(target-expected)*(1-Math.Exp(-.02/tau));Assert.That(e.Update(Second+i*20_000_000L,true,target,Second+i*20_000_000L).smoothedNavigationDown,Is.EqualTo(expected).Within(2e-5));}
    }

    [Test]
    public void EstimatorHotLoop_HasNoManagedAllocations()
    {
        var e=new VerticalAltitudeSmoothingEstimator();e.Initialize(Second);
        for(int i=1;i<=1000;i++)e.Update(Second+i*1000L,true,.1f,Second+i*1000L);
        var controlBefore=GC.GetAllocatedBytesForCurrentThread();var control=new byte[8192];GC.KeepAlive(control);
        var counterAvailable=GC.GetAllocatedBytesForCurrentThread()>controlBefore;
        var watch=Stopwatch.StartNew();var before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=1001;i<=501000;i++)e.Update(Second+i*1000L,true,.1f,Second+i*1000L);
        var allocated=GC.GetAllocatedBytesForCurrentThread()-before;watch.Stop();
        if(counterAvailable)Assert.That(allocated,Is.Zero);
        Assert.That(e.IsInitialized,Is.True);TestContext.WriteLine($"500000 updates: {watch.Elapsed.TotalMilliseconds:R} ms; allocation counter available={counterAvailable}; reported bytes={allocated}");
    }
}
