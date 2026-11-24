using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public sealed class FastHorizontalGnssConfigurationTests
{
    private const long Ms = 1_000_000;
    private static HorizontalGnssSmoothingEstimator New()
    {
        var e = new HorizontalGnssSmoothingEstimator();
        e.Initialize(1000 * Ms); e.Update(1000 * Ms, true, Vector2.zero, 1000 * Ms);
        return e;
    }

    [TestCase(.001f)] [TestCase(.025f)] [TestCase(.039f)]
    public void BelowFourCentimetersRemainsNoiseEvenAcrossDistinctCallbacks(float distance)
    {
        var e=New();
        for(int i=1;i<=4;i++)
        {
            var r=e.Update((1000+i*200)*Ms,true,new Vector2(distance,0),(1000+i*200)*Ms);
            Assert.That(r.targetAccepted,Is.False);
            Assert.That(r.targetRejectedReason,Is.EqualTo("WITHIN_STATIONARY_NOISE_ENVELOPE"));
            Assert.That(r.smoothedNavigationDelta,Is.EqualTo(Vector2.zero));
        }
    }

    [TestCase(.04f)] [TestCase(.041f)] [TestCase(.06f)]
    public void FourCentimetersAndAboveRequireTwoDistinctConsistentSamples(float distance)
    {
        var e=New(); var p=new Vector2(distance,0);
        var first=e.Update(1200*Ms,true,p,1200*Ms);
        Assert.That(first.targetAccepted,Is.False); Assert.That(first.temporalConsistencyCount,Is.EqualTo(1));
        var duplicate=e.Update(1300*Ms,true,p,1200*Ms);
        Assert.That(duplicate.targetAccepted,Is.False); Assert.That(duplicate.temporalConsistencyCount,Is.EqualTo(1));
        var second=e.Update(1400*Ms,true,p,1400*Ms);
        Assert.That(second.targetAccepted,Is.True); Assert.That(second.acceptedGnssTarget,Is.EqualTo(p));
    }

    [TestCase(59f,true)] [TestCase(61f,false)] [TestCase(90f,false)] [TestCase(180f,false)]
    public void ExistingDirectionDotProductBoundaryRemainsUnchanged(float angle,bool accepted)
    {
        var e=New();e.Update(1200*Ms,true,Vector2.right*.06f,1200*Ms);
        var p=new Vector2(Mathf.Cos(angle*Mathf.Deg2Rad),Mathf.Sin(angle*Mathf.Deg2Rad))*.06f;
        var r=e.Update(1400*Ms,true,p,1400*Ms);
        Assert.That(r.targetAccepted,Is.EqualTo(accepted));
        if(!accepted)Assert.That(r.temporalConsistencyCount,Is.EqualTo(1));
    }

    [Test]
    public void NumericalResponseUsesQuarterSecondTauBeforeAndAfterAdmission()
    {
        var e=New();
        var pending=e.Update(1200*Ms,true,new Vector2(.10f,0),1200*Ms);
        Assert.That(pending.smoothedNavigationDelta,Is.EqualTo(Vector2.zero));
        var admitted=e.Update(1400*Ms,true,new Vector2(.12f,0),1400*Ms);
        double expected=.12*(1-Math.Exp(-.2/.25));
        Assert.That(admitted.smoothedNavigationDelta.x,Is.EqualTo(expected).Within(1e-7));
        var next=e.Update(1420*Ms,true,new Vector2(.12f,0),1400*Ms);
        expected+=(.12-expected)*(1-Math.Exp(-.02/.25));
        Assert.That(next.smoothedNavigationDelta.x,Is.EqualTo(expected).Within(1e-7));
        Assert.That(next.tauSeconds,Is.EqualTo(.25f));
    }

    [Test]
    public void LargeConfirmedJumpRemainsSpeedLimitedAtEveryEvaluation()
    {
        var e=New();e.Update(1200*Ms,true,Vector2.right*100,1200*Ms);
        var previous=Vector2.zero;
        for(int t=1220;t<=3220;t+=20)
        {
            var r=e.Update(t*Ms,true,Vector2.right*100,t*Ms);
            Assert.That(Vector2.Distance(r.smoothedNavigationDelta,previous),Is.LessThanOrEqualTo(.050001f));
            Assert.That(r.correctionSpeedMetersPerSecond,Is.LessThanOrEqualTo(2.50001f));
            Assert.That(float.IsFinite(r.smoothedNavigationDelta.x),Is.True);
            previous=r.smoothedNavigationDelta;
        }
        Assert.That(previous.x,Is.LessThan(5.1f));
    }

    [Test]
    public void StaleAndQualityHoldDoNotTurnPauseIntoCatchUpStep()
    {
        var e=New(); e.Update(1200*Ms,true,Vector2.right,1200*Ms);
        var initial=e.Update(1400*Ms,true,Vector2.right,1400*Ms);
        var stale=e.Update(3000*Ms,false,Vector2.zero,0);
        var held=e.FreezeForGnssQuality(5000*Ms,"SATELLITE_COUNT_BELOW_ENTER_THRESHOLD");
        Assert.That(stale.smoothedNavigationDelta,Is.EqualTo(initial.smoothedNavigationDelta));
        Assert.That(held.smoothedNavigationDelta,Is.EqualTo(initial.smoothedNavigationDelta));
        Assert.That(held.state,Is.EqualTo(HorizontalGnssSmoothingState.DegradedGnssQuality));
        var resume=e.Update(5020*Ms,true,Vector2.right,5020*Ms);
        Assert.That(Vector2.Distance(resume.smoothedNavigationDelta,held.smoothedNavigationDelta),Is.LessThanOrEqualTo(.050001f));
    }

    [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)] [TestCase(float.NegativeInfinity)]
    public void InvalidRawCoordinatesNeverEnterTheOutput(float invalid)
    {
        var r=New().Update(1200*Ms,true,new Vector2(invalid,0),1200*Ms);
        Assert.That(r.valid,Is.False);Assert.That(r.smoothedNavigationDelta,Is.EqualTo(Vector2.zero));
    }

    [Test]
    public void FutureOrOutOfOrderEvaluationCannotIntroduceAnUnboundedCorrection()
    {
        var e=New();
        Assert.That(e.Update(1200*Ms,true,Vector2.one,1201*Ms).valid,Is.False);
        Assert.That(e.Update(1199*Ms,true,Vector2.one,1199*Ms).failureReason,Is.EqualTo("NON_MONOTONIC_TARGET"));
        Assert.That(DjiAircraftPositionFreshness.IsFreshAtOrBefore(1000*Ms,1300*Ms,300,out _),Is.True);
        Assert.That(DjiAircraftPositionFreshness.IsFreshAtOrBefore(1000*Ms,1301*Ms,300,out _),Is.False);
    }

    [Test]
    public void ProductionCoordinatorUsesTheNewDefaultWithoutAnotherConfigurationPath()
    {
        var c=new DjiWorldLocalizationShadowCoordinator();
        var positions=new List<TelemetryAircraftPositionSample> {new(1000*Ms,47,19,100),new(1200*Ms,47.000001,19,100),new(1400*Ms,47.000002,19,100)};
        c.ObserveHorizontalGnssQuality(23,600*Ms); c.ObserveHorizontalGnssQuality(23,800*Ms); c.ObserveHorizontalGnssQuality(23,1000*Ms);
        Assert.That(c.TryInitializeFromVisualMeasurement(Pose.identity,1000*Ms,true,out var reason),Is.True,reason);
        Assert.That(c.TryBeginRotationPropagation(Pose.identity,1000*Ms,Quaternion.identity,positions,300,out reason),Is.True,reason);
        Assert.That(c.TryEvaluateRotationPropagation(1200*Ms,1200*Ms,Quaternion.identity,Pose.identity,positions,300,out reason),Is.True,reason);
        Assert.That(c.LatestHorizontalGnssSmoothingShadow.estimate.targetAccepted,Is.False);
        Assert.That(c.TryEvaluateRotationPropagation(1400*Ms,1400*Ms,Quaternion.identity,Pose.identity,positions,300,out reason),Is.True,reason);
        var r=c.LatestHorizontalGnssSmoothingShadow.estimate;
        Assert.That(r.targetAccepted,Is.True);Assert.That(r.tauSeconds,Is.EqualTo(.25f));
        Assert.That(r.maximumCorrectionSpeedMetersPerSecond,Is.EqualTo(2.5f));
        Assert.That(c.ProductionHorizontalGnssApplied,Is.True);
        Assert.That(VerticalAltitudeSmoothingEstimator.DefaultMode,Is.EqualTo(VerticalAltitudeSmoothingMode.RawTarget));
        Assert.That(HorizontalGnssQualityAdmissionGate.ProvisionalEnterSatelliteCount,Is.EqualTo(23));
        Assert.That(HorizontalGnssQualityAdmissionGate.ProvisionalExitSatelliteCount,Is.EqualTo(21));
        Assert.That(HorizontalGnssSmoothingEstimator.ProvisionalTemporalConsistencySampleCount,Is.EqualTo(2));
    }
}
