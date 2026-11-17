using NUnit.Framework;

public sealed class VerticalAltitudeSmoothingEstimatorTests
{
    private const long Millisecond = 1_000_000L;

    [Test]
    public void Defaults_UseRawTargetAndPreserveFreshnessLimit()
    {
        var estimator = new VerticalAltitudeSmoothingEstimator();
        Assert.That(estimator.Initialize(100L * Millisecond), Is.True);

        var result = estimator.Update(500L * Millisecond, true, -1f, 500L * Millisecond);

        Assert.That(estimator.Mode, Is.EqualTo(VerticalAltitudeSmoothingMode.RawTarget));
        Assert.That(result.tauSeconds, Is.Zero);
        Assert.That(result.alpha, Is.EqualTo(1f));
        Assert.That(result.smoothedNavigationDown, Is.EqualTo(-1f));
        Assert.That(VerticalAltitudeSmoothingEstimator.DefaultMaximumAgeMilliseconds, Is.EqualTo(300f));
    }

    [Test]
    public void PositiveAltitudeUp_UsesNegativeNavigationDownTarget()
    {
        var estimator = new VerticalAltitudeSmoothingEstimator();
        estimator.Initialize(100L * Millisecond);

        var result = estimator.Update(500L * Millisecond, true, -1f, 500L * Millisecond);

        Assert.That(result.rawTargetNavigationDown, Is.EqualTo(-1f));
        Assert.That(result.smoothedNavigationDown, Is.LessThan(0f));
    }

    [Test]
    public void NegativeAltitudeUp_UsesPositiveNavigationDownTarget()
    {
        var estimator = new VerticalAltitudeSmoothingEstimator();
        estimator.Initialize(100L * Millisecond);

        var result = estimator.Update(500L * Millisecond, true, 1f, 500L * Millisecond);

        Assert.That(result.rawTargetNavigationDown, Is.EqualTo(1f));
        Assert.That(result.smoothedNavigationDown, Is.GreaterThan(0f));
    }

    [Test]
    public void ZeroMotion_RemainsAtBaseline()
    {
        var estimator = new VerticalAltitudeSmoothingEstimator();
        estimator.Initialize(100L * Millisecond);

        var result = estimator.Update(500L * Millisecond, true, 0f, 500L * Millisecond);

        Assert.That(result.state, Is.EqualTo(VerticalAltitudeLocalizationState.Active));
        Assert.That(result.smoothedNavigationDown, Is.EqualTo(0f));
    }

    [Test]
    public void TenthMeterStep_UsesExponentialConvergence()
    {
        var estimator = new VerticalAltitudeSmoothingEstimator(0.4f);
        estimator.Initialize(100L * Millisecond);

        var result = estimator.Update(500L * Millisecond, true, -0.1f, 500L * Millisecond);

        Assert.That(result.smoothedNavigationDown, Is.EqualTo(-0.1f * (1f - UnityEngine.Mathf.Exp(-1f))).Within(0.0001f));
    }

    [Test]
    public void DuplicateAltitudeTimestamp_DoesNotReingestButContinuesSmoothing()
    {
        var estimator = new VerticalAltitudeSmoothingEstimator(0.3f);
        estimator.Initialize(100L * Millisecond);
        estimator.Update(200L * Millisecond, true, -1f, 200L * Millisecond);

        var repeated = estimator.Update(300L * Millisecond, true, 123f, 200L * Millisecond);

        Assert.That(repeated.sourceAccepted, Is.False);
        Assert.That(repeated.rawTargetNavigationDown, Is.EqualTo(-1f));
        Assert.That(repeated.smoothedNavigationDown, Is.LessThan(0f));
        Assert.That(repeated.smoothedNavigationDown, Is.GreaterThan(-1f));
    }

    [Test]
    public void StaleAltitude_FreezesAndFreshRecoveryResumesWithoutReset()
    {
        var estimator = new VerticalAltitudeSmoothingEstimator();
        estimator.Initialize(100L * Millisecond);
        var first = estimator.Update(500L * Millisecond, true, -1f, 500L * Millisecond);

        var stale = estimator.Update(700L * Millisecond, false, 0f, 0L);
        var resumed = estimator.Update(900L * Millisecond, true, -2f, 900L * Millisecond);

        Assert.That(stale.state, Is.EqualTo(VerticalAltitudeLocalizationState.DegradedStale));
        Assert.That(stale.smoothedNavigationDown, Is.EqualTo(first.smoothedNavigationDown));
        Assert.That(stale.desiredCorrection, Is.Zero);
        Assert.That(resumed.state, Is.EqualTo(VerticalAltitudeLocalizationState.Active));
        Assert.That(resumed.smoothedNavigationDown, Is.EqualTo(-2f));
        Assert.That(resumed.alpha, Is.EqualTo(1f));
    }
}
