using NUnit.Framework;
using UnityEngine;

public sealed class WorldPathEquivalenceDiagnosticsTests
{
    private const float Tolerance = 0.0001f;

    [Test]
    public void RotationPropagationDiagnostics_EquivalentPosesRemainAtNumericalNoise()
    {
        var diagnostics = new WorldRotationPropagationEquivalenceDiagnostics();
        var pose = new Pose(new Vector3(0.2f, -0.3f, 1.4f), Quaternion.Euler(11f, -20f, 31f));

        Assert.That(diagnostics.TryRecord(100L, pose, pose), Is.True);

        Assert.That(diagnostics.Snapshot.valid, Is.True);
        Assert.That(diagnostics.Snapshot.sampleCount, Is.EqualTo(1));
        Assert.That(diagnostics.Snapshot.maxTranslationResidualM, Is.LessThanOrEqualTo(Tolerance));
        Assert.That(diagnostics.Snapshot.maxRotationResidualDeg, Is.LessThanOrEqualTo(Tolerance));
    }

    [Test]
    public void RotationPropagationDiagnostics_RejectsInvalidTimestamp()
    {
        var diagnostics = new WorldRotationPropagationEquivalenceDiagnostics();

        Assert.That(diagnostics.TryRecord(0L, Pose.identity, Pose.identity), Is.False);
        Assert.That(diagnostics.Snapshot.valid, Is.False);
        Assert.That(diagnostics.Snapshot.sampleCount, Is.EqualTo(0));
    }
}
