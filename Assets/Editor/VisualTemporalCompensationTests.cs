using NUnit.Framework;
using UnityEngine;

public sealed class VisualTemporalCompensationTests
{
    private const long OneSecondNs = 1_000_000_000L;

    [TestCase(0L)]
    [TestCase(40L)]
    [TestCase(70L)]
    [TestCase(100L)]
    public void ZeroMotion_HorizonsKeepTheRawPose(long horizonMilliseconds)
    {
        var pose = new Pose(new Vector3(1f, -2f, 3f), Quaternion.Euler(12f, -23f, 34f));
        var predictor = CreatePredictor(pose, pose);

        Assert.That(TryPredict(predictor, horizonMilliseconds, out var predicted), Is.True);
        AssertVectorApproximately(predicted.position, pose.position);
        Assert.That(Quaternion.Angle(predicted.rotation, pose.rotation), Is.EqualTo(0f).Within(0.0001f));
    }

    [TestCase(0, 1f, 40L)]
    [TestCase(0, -1f, 70L)]
    [TestCase(1, 1f, 100L)]
    [TestCase(1, -1f, 40L)]
    [TestCase(2, 1f, 70L)]
    [TestCase(2, -1f, 100L)]
    public void TranslationPrediction_PreservesCameraFromBoardAxisAndSign(int axis, float sign, long horizonMilliseconds)
    {
        var direction = Vector3.zero;
        direction[axis] = sign;
        var predictor = CreatePredictor(Pose.identity, new Pose(direction, Quaternion.identity));

        Assert.That(TryPredict(predictor, horizonMilliseconds, out var predicted), Is.True);
        AssertVectorApproximately(predicted.position, direction * (1f + horizonMilliseconds / 1000f));
    }

    [Test]
    public void TranslationPrediction_PhysicalScaleIsOneMeterPerSecondAtSeventyMilliseconds()
    {
        var predictor = CreatePredictor(Pose.identity, new Pose(Vector3.right, Quaternion.identity));

        Assert.That(TryPredict(predictor, 70L, out var predicted), Is.True);
        Assert.That(predicted.position.x - 1f, Is.EqualTo(0.070f).Within(0.0001f));
    }

    [TestCase(0f, 90f, 0f)]
    [TestCase(0f, -90f, 0f)]
    [TestCase(60f, 0f, 0f)]
    [TestCase(0f, 0f, -60f)]
    public void RotationPrediction_UsesQuaternionDeltaWithoutEulerExtrapolation(float x, float y, float z)
    {
        var target = Quaternion.Euler(x, y, z);
        var predictor = CreatePredictor(Pose.identity, new Pose(Vector3.zero, target));

        Assert.That(TryPredict(predictor, 100L, out var predicted), Is.True);
        var expected = Quaternion.SlerpUnclamped(Quaternion.identity, target, 1.1f);
        Assert.That(Quaternion.Angle(predicted.rotation, expected), Is.EqualTo(0f).Within(0.001f));
    }

    [Test]
    public void RotationPrediction_AcrossOneEightyDegreesUsesShortestQuaternionArc()
    {
        var predictor = CreatePredictor(
            new Pose(Vector3.zero, Quaternion.Euler(0f, 170f, 0f)),
            new Pose(Vector3.zero, Quaternion.Euler(0f, -170f, 0f)));

        Assert.That(TryPredict(predictor, 100L, out var predicted), Is.True);
        var expected = Quaternion.Euler(0f, -168f, 0f);
        Assert.That(Quaternion.Angle(predicted.rotation, expected), Is.LessThan(0.01f));
    }

    [Test]
    public void HorizonAboveConfiguredMaximum_DisablesPredictionRatherThanClampingIt()
    {
        var predictor = CreatePredictor(Pose.identity, new Pose(Vector3.right, Quaternion.identity));

        Assert.That(TryPredict(predictor, 101L, out _), Is.False);
    }

    [Test]
    public void SuddenStop_UsesNewestTwoVisualSamplesAndRapidlyReturnsToZeroVelocity()
    {
        var predictor = new VisualTemporalCompensationPredictor();
        predictor.AddAcceptedVisualPose(1L, OneSecondNs, Pose.identity);
        predictor.AddAcceptedVisualPose(2L, 2L * OneSecondNs, new Pose(Vector3.right, Quaternion.identity));
        predictor.AddAcceptedVisualPose(3L, 3L * OneSecondNs, new Pose(Vector3.right, Quaternion.identity));

        Assert.That(TryPredict(predictor, 3L, 3L * OneSecondNs, 70L, out var predicted), Is.True);
        AssertVectorApproximately(predicted.position, Vector3.right);
    }

    [Test]
    public void ResetAndReacquisition_DoNotReusePreOutageVelocity()
    {
        var predictor = CreatePredictor(Pose.identity, new Pose(Vector3.right, Quaternion.identity));
        predictor.Clear();
        predictor.AddAcceptedVisualPose(10L, 10L * OneSecondNs, new Pose(new Vector3(100f, 0f, 0f), Quaternion.identity));

        Assert.That(TryPredict(predictor, 10L, 10L * OneSecondNs, 70L, out _), Is.False);

        predictor.AddAcceptedVisualPose(11L, 11L * OneSecondNs, new Pose(new Vector3(100.1f, 0f, 0f), Quaternion.identity));
        Assert.That(TryPredict(predictor, 11L, 11L * OneSecondNs, 70L, out var predicted), Is.True);
        Assert.That(predicted.position.x, Is.EqualTo(100.107f).Within(0.001f));
    }

    private static VisualTemporalCompensationPredictor CreatePredictor(Pose previous, Pose current)
    {
        var predictor = new VisualTemporalCompensationPredictor();
        predictor.AddAcceptedVisualPose(1L, OneSecondNs, previous);
        predictor.AddAcceptedVisualPose(2L, 2L * OneSecondNs, current);
        return predictor;
    }

    private static bool TryPredict(VisualTemporalCompensationPredictor predictor, long horizonMilliseconds, out Pose predicted) =>
        TryPredict(predictor, 2L, 2L * OneSecondNs, horizonMilliseconds, out predicted);

    private static bool TryPredict(
        VisualTemporalCompensationPredictor predictor,
        long currentSourceFrameId,
        long sourceTimestampNs,
        long horizonMilliseconds,
        out Pose predicted)
    {
        var displayTimestampNs = sourceTimestampNs + horizonMilliseconds * 1_000_000L;
        return predictor.TryPredict(
            currentSourceFrameId,
            displayTimestampNs,
            displayTimestampNs,
            CreateTestConfiguration(),
            out predicted,
            out _,
            out _,
            out _,
            out _);
    }

    private static VisualTemporalCompensationConfiguration CreateTestConfiguration() => new()
    {
        // The synthetic fixtures use one-second source intervals to make
        // velocity and quaternion extrapolation expectations easy to audit.
        maxVisualSampleIntervalMilliseconds = 1_500f
    };

    private static void AssertVectorApproximately(Vector3 actual, Vector3 expected) =>
        Assert.That(Vector3.Distance(actual, expected), Is.LessThan(0.0001f));
}
