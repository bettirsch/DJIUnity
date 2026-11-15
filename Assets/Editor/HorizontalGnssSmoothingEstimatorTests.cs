using NUnit.Framework;
using UnityEngine;

public sealed class HorizontalGnssSmoothingEstimatorTests
{
    private const long Millisecond = 1_000_000L;

    [Test]
    public void Defaults_AreTheProvisionalGnssOnlyValues()
    {
        var estimator = new HorizontalGnssSmoothingEstimator();
        Assert.That(estimator.Initialize(100L * Millisecond), Is.True);

        var result = estimator.Update(200L * Millisecond, true, Vector2.zero, 200L * Millisecond);

        Assert.That(result.tauSeconds, Is.EqualTo(0.5f));
        Assert.That(result.maximumCorrectionSpeedMetersPerSecond, Is.EqualTo(2.5f));
    }

    [Test]
    public void ZeroMotion_RemainsAtOrigin()
    {
        var estimator = new HorizontalGnssSmoothingEstimator();
        Assert.That(estimator.Initialize(100L * Millisecond), Is.True);

        var result = estimator.Update(600L * Millisecond, true, Vector2.zero, 600L * Millisecond);

        Assert.That(result.valid, Is.True);
        Assert.That(result.state, Is.EqualTo(HorizontalGnssSmoothingState.FollowingFreshGnss));
        Assert.That(result.smoothedNavigationDelta, Is.EqualTo(Vector2.zero));
        Assert.That(result.correctionSpeedMetersPerSecond, Is.EqualTo(0f));
    }

    [Test]
    public void FreshTarget_UsesExponentialConvergence()
    {
        var estimator = new HorizontalGnssSmoothingEstimator();
        Assert.That(estimator.Initialize(100L * Millisecond), Is.True);

        var result = estimator.Update(600L * Millisecond, true, new Vector2(1f, 0f), 600L * Millisecond);

        Assert.That(result.smoothedNavigationDelta.x, Is.EqualTo(1f - Mathf.Exp(-1f)).Within(0.0001f));
        Assert.That(result.appliedCorrection.x, Is.EqualTo(result.desiredCorrection.x).Within(0.0001f));
    }

    [Test]
    public void LargeTarget_UsesMaximumCorrectionSpeedClamp()
    {
        var estimator = new HorizontalGnssSmoothingEstimator();
        Assert.That(estimator.Initialize(100L * Millisecond), Is.True);

        var result = estimator.Update(600L * Millisecond, true, new Vector2(100f, 0f), 600L * Millisecond);

        Assert.That(result.desiredCorrection.magnitude, Is.GreaterThan(1.25f));
        Assert.That(result.appliedCorrection.magnitude, Is.EqualTo(1.25f).Within(0.0001f));
        Assert.That(result.correctionSpeedMetersPerSecond, Is.EqualTo(2.5f).Within(0.0001f));
    }

    [Test]
    public void IdenticalTimestamp_DoesNotDoubleUpdate()
    {
        var estimator = new HorizontalGnssSmoothingEstimator();
        Assert.That(estimator.Initialize(100L * Millisecond), Is.True);
        var first = estimator.Update(600L * Millisecond, true, new Vector2(1f, 0f), 600L * Millisecond);

        var repeated = estimator.Update(600L * Millisecond, true, new Vector2(1f, 0f), 600L * Millisecond);

        Assert.That(repeated.smoothedNavigationDelta, Is.EqualTo(first.smoothedNavigationDelta));
        Assert.That(repeated.appliedCorrection, Is.EqualTo(Vector2.zero));
    }

    [Test]
    public void StaleGnss_FreezesEstimateAndFreshGnssResumesWithoutReset()
    {
        var estimator = new HorizontalGnssSmoothingEstimator();
        Assert.That(estimator.Initialize(100L * Millisecond), Is.True);
        var first = estimator.Update(600L * Millisecond, true, new Vector2(1f, 0f), 600L * Millisecond);

        var stale = estimator.Update(1100L * Millisecond, false, Vector2.zero, 0L);
        var resumedPending = estimator.Update(1200L * Millisecond, true, new Vector2(2f, 0f), 1200L * Millisecond);
        var resumed = estimator.Update(1300L * Millisecond, true, new Vector2(2.1f, 0f), 1300L * Millisecond);

        Assert.That(stale.valid, Is.False);
        Assert.That(stale.state, Is.EqualTo(HorizontalGnssSmoothingState.DegradedStaleGnss));
        Assert.That(stale.smoothedNavigationDelta, Is.EqualTo(first.smoothedNavigationDelta));
        Assert.That(resumedPending.targetAccepted, Is.False);
        Assert.That(resumed.valid, Is.True);
        Assert.That(resumed.targetAccepted, Is.True);
        Assert.That(resumed.smoothedNavigationDelta.x, Is.GreaterThan(first.smoothedNavigationDelta.x));
        Assert.That(resumed.smoothedNavigationDelta.x, Is.LessThan(2f));
    }

    [Test]
    public void Estimate_IsHorizontalOnly()
    {
        var estimator = new HorizontalGnssSmoothingEstimator();
        Assert.That(estimator.Initialize(100L * Millisecond), Is.True);

        var result = estimator.Update(200L * Millisecond, true, new Vector2(0.5f, -0.25f), 200L * Millisecond);

        Assert.That(result.smoothedNavigationDelta, Is.TypeOf<Vector2>());
        Assert.That(result.rawGnssTarget, Is.TypeOf<Vector2>());
    }

    [Test]
    public void SmallStationaryJitter_IsRejectedWithoutMovingAcceptedTarget()
    {
        var estimator = new HorizontalGnssSmoothingEstimator();
        estimator.Initialize(100L * Millisecond);
        estimator.Update(200L * Millisecond, true, Vector2.zero, 200L * Millisecond);

        var jitter = estimator.Update(400L * Millisecond, true, new Vector2(0.04f, -0.03f), 400L * Millisecond);

        Assert.That(jitter.targetAccepted, Is.False);
        Assert.That(jitter.targetRejectedReason, Is.EqualTo("WITHIN_STATIONARY_NOISE_ENVELOPE"));
        Assert.That(jitter.acceptedGnssTarget, Is.EqualTo(Vector2.zero));
        Assert.That(jitter.smoothedNavigationDelta, Is.EqualTo(Vector2.zero));
    }

    [Test]
    public void SingleGnssSpike_IsRejectedUntilMotionIsTemporallyConsistent()
    {
        var estimator = new HorizontalGnssSmoothingEstimator();
        estimator.Initialize(100L * Millisecond);
        estimator.Update(200L * Millisecond, true, Vector2.zero, 200L * Millisecond);

        var spike = estimator.Update(400L * Millisecond, true, new Vector2(1f, 0f), 400L * Millisecond);
        var recovered = estimator.Update(600L * Millisecond, true, Vector2.zero, 600L * Millisecond);

        Assert.That(spike.targetAccepted, Is.False);
        Assert.That(spike.targetRejectedReason, Is.EqualTo("PENDING_TEMPORAL_CONSISTENCY"));
        Assert.That(recovered.targetAccepted, Is.False);
        Assert.That(recovered.acceptedGnssTarget, Is.EqualTo(Vector2.zero));
    }

    [Test]
    public void SustainedNorthMotion_IsAccepted()
    {
        var estimator = new HorizontalGnssSmoothingEstimator();
        estimator.Initialize(100L * Millisecond);
        estimator.Update(200L * Millisecond, true, Vector2.zero, 200L * Millisecond);

        var pending = estimator.Update(400L * Millisecond, true, new Vector2(0.12f, 0f), 400L * Millisecond);
        var accepted = estimator.Update(600L * Millisecond, true, new Vector2(0.20f, 0f), 600L * Millisecond);

        Assert.That(pending.targetAccepted, Is.False);
        Assert.That(accepted.targetAccepted, Is.True);
        Assert.That(accepted.acceptedGnssTarget, Is.EqualTo(new Vector2(0.20f, 0f)));
    }

    [Test]
    public void SustainedEastMotion_IsAccepted()
    {
        var estimator = new HorizontalGnssSmoothingEstimator();
        estimator.Initialize(100L * Millisecond);
        estimator.Update(200L * Millisecond, true, Vector2.zero, 200L * Millisecond);

        estimator.Update(400L * Millisecond, true, new Vector2(0f, 0.12f), 400L * Millisecond);
        var accepted = estimator.Update(600L * Millisecond, true, new Vector2(0f, 0.20f), 600L * Millisecond);

        Assert.That(accepted.targetAccepted, Is.True);
        Assert.That(accepted.acceptedGnssTarget, Is.EqualTo(new Vector2(0f, 0.20f)));
    }

    [Test]
    public void DirectionReversal_ResetsPendingConsistencyWithoutCorruptingAcceptedTarget()
    {
        var estimator = new HorizontalGnssSmoothingEstimator();
        estimator.Initialize(100L * Millisecond);
        estimator.Update(200L * Millisecond, true, Vector2.zero, 200L * Millisecond);

        estimator.Update(400L * Millisecond, true, new Vector2(0.2f, 0f), 400L * Millisecond);
        var reversal = estimator.Update(600L * Millisecond, true, new Vector2(0f, 0.2f), 600L * Millisecond);
        var acceptedEast = estimator.Update(800L * Millisecond, true, new Vector2(0f, 0.3f), 800L * Millisecond);

        Assert.That(reversal.targetAccepted, Is.False);
        Assert.That(reversal.temporalConsistencyCount, Is.EqualTo(1));
        Assert.That(reversal.acceptedGnssTarget, Is.EqualTo(Vector2.zero));
        Assert.That(acceptedEast.targetAccepted, Is.True);
        Assert.That(acceptedEast.acceptedGnssTarget, Is.EqualTo(new Vector2(0f, 0.3f)));
    }
}
