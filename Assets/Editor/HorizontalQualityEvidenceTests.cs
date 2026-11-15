using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public sealed class HorizontalQualityEvidenceTests
{
    private const long Ms = 1_000_000L;

    [Test]
    public void PhysicalSat21Episode_FreshTargetsAreBlockedBeforeTemporalConfirmation()
    {
        // Exact recorded callback/evaluation times and raw N/E targets. The
        // first two causal callbacks predate the reported hold interval.
        var evaluation = new long[] { 5186055426047338, 5186055479572569, 5186055585895145, 5186055813064184,
            5186056096572107, 5186056180495222, 5186056382089568, 5186056613939414, 5186056848158875,
            5186057082229606, 5186057278975490, 5186057413210567, 5186057648477298, 5186057782582067, 5186058013258721 };
        var callback = new long[] { 5186055175269876, 5186055351705799, 5186055577022222, 5186055761544261,
            5186055984190876, 5186056150461799, 5186056367962491, 5186056555843760, 5186056785051029,
            5186056952444106, 5186057181016567, 5186057354931567, 5186057583209413, 5186057754461144, 5186057985344605 };
        var raw = new[] { new Vector2(-.02846745f,.00888056f), new Vector2(-.0516812019f,.0133542791f),
            new Vector2(-.09100655f,.03127447f), new Vector2(-.144588366f,.06464901f), new Vector2(-.185639784f,.0909638256f),
            new Vector2(-.228126541f,.127203211f), new Vector2(-.2821586f,.181911051f), new Vector2(-.325999975f,.225751862f),
            new Vector2(-.356294453f,.253505856f), new Vector2(-.3743024f,.266868144f), new Vector2(-.385237426f,.2716895f),
            new Vector2(-.391131639f,.271503419f), new Vector2(-.393564433f,.267928362f), new Vector2(-.395628154f,.265087038f),
            new Vector2(-.3962146f,.264535785f) };
        var gate = new HorizontalGnssQualityAdmissionGate();
        var estimator = new HorizontalGnssSmoothingEstimator();
        estimator.Initialize(5186055007794453);
        estimator.Update(5186055007794453, true, Vector2.zero, 5186054945689184);
        for (var i = 0; i < raw.Length; i++)
        {
            var quality = gate.Update(callback[i], i < 2 ? 20 : i < 13 ? 21 : 22);
            Assert.That(DjiAircraftPositionFreshness.IsFreshAtOrBefore(callback[i], evaluation[i], 300, out _), Is.True);
            Assert.That(quality.trusted, Is.False);
            var held = estimator.FreezeForGnssQuality(evaluation[i], quality.admissionReason);
            Assert.That(held.smoothedNavigationDelta, Is.EqualTo(Vector2.zero));
            Assert.That(held.temporalConsistencyCount, Is.Zero);
            Assert.That(held.targetAccepted, Is.False);
        }
        Assert.That((evaluation[14]-evaluation[0])/1e9, Is.EqualTo(2.587211383).Within(1e-9));
        Assert.That(Vector2.Distance(raw[0],raw[14]), Is.EqualTo(.44788119f).Within(.000001f));
    }

    [Test]
    public void HysteresisAndCallbackConfirmation_AreUnchanged()
    {
        var gate = new HorizontalGnssQualityAdmissionGate();
        for (var i=1;i<=3;i++) gate.Update(i*Ms,23);
        Assert.That(gate.Update(4*Ms,22).trusted, Is.True);
        Assert.That(gate.Update(5*Ms,21).trusted, Is.True);
        Assert.That(gate.Update(5*Ms,21).confirmationCount, Is.EqualTo(1));
        Assert.That(gate.Update(6*Ms,21).trusted, Is.True);
        Assert.That(gate.Update(7*Ms,21).trusted, Is.False);
        Assert.That(gate.Update(8*Ms,22).trusted, Is.False);
    }

    [Test]
    public void SmoothCoherentDriftAndRealMovement_WithIdenticalInputsAreIndistinguishable()
    {
        var movement = NewEstimator(); var drift = NewEstimator();
        var admitted=0;
        for(var i=1;i<=30;i++)
        {
            var target=new Vector2(i*.03f,0);
            var a=movement.Update((100+i*200)*Ms,true,target,(100+i*200)*Ms);
            var b=drift.Update((100+i*200)*Ms,true,target,(100+i*200)*Ms);
            Assert.That(a.smoothedNavigationDelta,Is.EqualTo(b.smoothedNavigationDelta));
            if(a.targetAccepted) admitted++;
        }
        Assert.That(admitted,Is.GreaterThan(0),"Temporal consistency alone does not reject coherent drift.");
    }

    [Test]
    public void SmallStationaryJitter_IsNotAcceptedAsTargetMotion()
    {
        var estimator=NewEstimator();
        for(var i=1;i<=20;i++)
        {
            var result=estimator.Update((100+i*200)*Ms,true,new Vector2(i%2==0?.02f:-.02f,.01f),(100+i*200)*Ms);
            Assert.That(result.targetAccepted,Is.False);
            Assert.That(result.smoothedNavigationDelta,Is.EqualTo(Vector2.zero));
        }
    }

    [Test]
    public void IsolatedLargeJump_IsPendingThenCancelled_NotFedToSmoother()
    {
        var estimator=NewEstimator();
        var outlier=estimator.Update(300*Ms,true,new Vector2(10,0),300*Ms);
        Assert.That(outlier.targetAccepted,Is.False);
        Assert.That(outlier.targetRejectedReason,Is.EqualTo("PENDING_TEMPORAL_CONSISTENCY"));
        var recovered=estimator.Update(500*Ms,true,Vector2.zero,500*Ms);
        Assert.That(recovered.smoothedNavigationDelta,Is.EqualTo(Vector2.zero));
        Assert.That(recovered.temporalConsistencyCount,Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void StaleOrInvalidGnss_RemainsHeld(bool invalid)
    {
        var estimator=NewEstimator();
        var result=estimator.Update(500*Ms,invalid,invalid ? new Vector2(float.NaN,0) : Vector2.one,100*Ms);
        Assert.That(result.valid,Is.False);
        Assert.That(result.smoothedNavigationDelta,Is.EqualTo(Vector2.zero));
        Assert.That(DjiAircraftPositionFreshness.IsFreshAtOrBefore(100*Ms,500*Ms,300,out _),Is.False);
    }

    [Test]
    public void LowSatelliteHold_PreservesRotationAndAuthoritativeVisualReacquisition()
    {
        var c=new DjiWorldLocalizationShadowCoordinator();
        var p=new List<TelemetryAircraftPositionSample>{new(100*Ms,47,19,100),new(300*Ms,47.00001,19,100)};
        c.ObserveHorizontalGnssQuality(21,100*Ms);
        Assert.That(c.TryInitializeFromVisualMeasurement(Pose.identity,100*Ms,true,out _),Is.True);
        Assert.That(c.TryBeginRotationPropagation(Pose.identity,100*Ms,Quaternion.identity,p,300,out _),Is.True);
        Assert.That(c.TryEvaluateRotationPropagation(300*Ms,300*Ms,Quaternion.Euler(0,10,0),Pose.identity,p,300,out _),Is.True);
        Assert.That(c.Snapshot.phase,Is.EqualTo(WorldLocalizationPhase.Propagating));
        Assert.That(c.Snapshot.worldFromCamera.position,Is.EqualTo(Vector3.zero));
        Assert.That(Quaternion.Angle(c.Snapshot.worldFromCamera.rotation,Quaternion.identity),Is.GreaterThan(1));
        Assert.That(c.Snapshot.worldFromBoard.position,Is.EqualTo(Vector3.zero));
        var visual=new Pose(new Vector3(1,2,3),Quaternion.Euler(1,2,3));
        Assert.That(c.TryUpdateFromVisualMeasurement(visual,400*Ms,true,p,300,out _),Is.True);
        Assert.That(c.Snapshot.phase,Is.EqualTo(WorldLocalizationPhase.VisualLocalized));
        Assert.That(Vector3.Distance(c.Snapshot.worldFromCamera.position,ReferenceFrameTransforms.Invert(visual).position),Is.LessThan(.00001f));
    }

    private static HorizontalGnssSmoothingEstimator NewEstimator()
    {
        var estimator=new HorizontalGnssSmoothingEstimator();
        estimator.Initialize(100*Ms);estimator.Update(100*Ms,true,Vector2.zero,100*Ms);
        return estimator;
    }
}
