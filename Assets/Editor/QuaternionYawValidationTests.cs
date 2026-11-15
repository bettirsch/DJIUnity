using NUnit.Framework;
using UnityEngine;

public sealed class QuaternionYawValidationTests
{
    [Test]
    public void SignedTwist_ReturnsPositiveTenDegreesAroundValidationAxis()
    {
        var signedYaw = QuaternionYawValidation.SignedTwistDegrees(
            Quaternion.AngleAxis(10f, QuaternionYawValidation.NavigationYawAxis),
            QuaternionYawValidation.NavigationYawAxis);

        Assert.That(signedYaw, Is.EqualTo(10f).Within(0.001f));
    }

    [Test]
    public void SignedTwist_ReturnsNegativeTenDegreesAroundValidationAxis()
    {
        var signedYaw = QuaternionYawValidation.SignedTwistDegrees(
            Quaternion.AngleAxis(-10f, QuaternionYawValidation.NavigationYawAxis),
            QuaternionYawValidation.NavigationYawAxis);

        Assert.That(signedYaw, Is.EqualTo(-10f).Within(0.001f));
    }
}
