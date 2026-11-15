using NUnit.Framework;

public sealed class HorizontalGnssQualityAdmissionGateTests
{
    [Test]
    public void BelowEnterThreshold_RemainsUntrustedAndDoesNotCountPollingDuplicates()
    {
        var gate = new HorizontalGnssQualityAdmissionGate();

        gate.Update(100L, 22);
        gate.Update(100L, 23);
        var snapshot = gate.Update(200L, 23);

        Assert.That(snapshot.trusted, Is.False);
        Assert.That(snapshot.confirmationCount, Is.EqualTo(1));
        Assert.That(snapshot.admissionReason, Is.EqualTo("SATELLITE_ENTER_CONFIRMING"));
    }

    [Test]
    public void ThreeQualifyingCallbacks_EnterTrusted()
    {
        var gate = new HorizontalGnssQualityAdmissionGate();

        gate.Update(100L, 23);
        gate.Update(200L, 24);
        var snapshot = gate.Update(300L, 25);

        Assert.That(snapshot.trusted, Is.True);
        Assert.That(snapshot.transition, Is.EqualTo(HorizontalGnssQualityTransition.EnteredTrusted));
        Assert.That(snapshot.confirmationCount, Is.EqualTo(3));
    }

    [Test]
    public void TrustedAtTwentyTwo_RemainsTrustedThroughHysteresis()
    {
        var gate = CreateTrustedGate();

        var snapshot = gate.Update(400L, 22);

        Assert.That(snapshot.trusted, Is.True);
        Assert.That(snapshot.confirmationCount, Is.Zero);
        Assert.That(snapshot.admissionReason, Is.EqualTo("SATELLITE_HYSTERESIS_HOLD"));
    }

    [Test]
    public void ThreeWeakCallbacks_ExitTrusted()
    {
        var gate = CreateTrustedGate();

        gate.Update(400L, 21);
        gate.Update(500L, 20);
        var snapshot = gate.Update(600L, 18);

        Assert.That(snapshot.trusted, Is.False);
        Assert.That(snapshot.transition, Is.EqualTo(HorizontalGnssQualityTransition.ExitedTrusted));
        Assert.That(snapshot.admissionReason, Is.EqualTo("SATELLITE_EXIT_CONFIRMED"));
    }

    [Test]
    public void Recovery_RequiresFreshConfirmationWithoutReset()
    {
        var gate = CreateTrustedGate();
        gate.Update(400L, 21);
        gate.Update(500L, 21);
        gate.Update(600L, 21);

        gate.Update(700L, 23);
        gate.Update(800L, 23);
        var recovered = gate.Update(900L, 23);

        Assert.That(recovered.trusted, Is.True);
        Assert.That(recovered.transition, Is.EqualTo(HorizontalGnssQualityTransition.EnteredTrusted));
    }

    private static HorizontalGnssQualityAdmissionGate CreateTrustedGate()
    {
        var gate = new HorizontalGnssQualityAdmissionGate();
        gate.Update(100L, 23);
        gate.Update(200L, 23);
        gate.Update(300L, 23);
        return gate;
    }
}
