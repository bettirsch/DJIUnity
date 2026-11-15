using UnityEngine;

/// <summary>
/// Immutable residuals between the existing production rotation-only pose and
/// the same pose reconstructed through persistent World state. Diagnostics
/// only: this has no render-routing authority.
/// </summary>
public readonly struct WorldRotationPropagationEquivalenceSnapshot
{
    public readonly bool valid;
    public readonly long telemetryTimestampNs;
    public readonly int sampleCount;
    public readonly float latestTranslationResidualM;
    public readonly float latestRotationResidualDeg;
    public readonly float maxTranslationResidualM;
    public readonly float maxRotationResidualDeg;
    public readonly long productionBaselineTimestampNs;
    public readonly long resolvedWorldBaselineTimestampNs;
    public readonly bool historicalBaselineResolved;
    public readonly float worldBaselineHistoryAgeMs;
    public readonly int worldBaselineHistoryCount;
    public readonly string baselineResolveFailureReason;

    public WorldRotationPropagationEquivalenceSnapshot(
        bool valid,
        long telemetryTimestampNs,
        int sampleCount,
        float latestTranslationResidualM,
        float latestRotationResidualDeg,
        float maxTranslationResidualM,
        float maxRotationResidualDeg,
        long productionBaselineTimestampNs = 0L,
        long resolvedWorldBaselineTimestampNs = 0L,
        bool historicalBaselineResolved = false,
        float worldBaselineHistoryAgeMs = 0f,
        int worldBaselineHistoryCount = 0,
        string baselineResolveFailureReason = "")
    {
        this.valid = valid;
        this.telemetryTimestampNs = telemetryTimestampNs;
        this.sampleCount = sampleCount;
        this.latestTranslationResidualM = latestTranslationResidualM;
        this.latestRotationResidualDeg = latestRotationResidualDeg;
        this.maxTranslationResidualM = maxTranslationResidualM;
        this.maxRotationResidualDeg = maxRotationResidualDeg;
        this.productionBaselineTimestampNs = productionBaselineTimestampNs;
        this.resolvedWorldBaselineTimestampNs = resolvedWorldBaselineTimestampNs;
        this.historicalBaselineResolved = historicalBaselineResolved;
        this.worldBaselineHistoryAgeMs = worldBaselineHistoryAgeMs;
        this.worldBaselineHistoryCount = worldBaselineHistoryCount;
        this.baselineResolveFailureReason = baselineResolveFailureReason ?? string.Empty;
    }
}

/// <summary>
/// Pure accumulator for production-versus-world rotation-propagation parity.
/// It receives poses that were already predicted by the production helper and
/// deliberately does not implement rotation propagation itself.
/// </summary>
public sealed class WorldRotationPropagationEquivalenceDiagnostics
{
    public WorldRotationPropagationEquivalenceSnapshot Snapshot { get; private set; }

    public bool TryRecord(
        long telemetryTimestampNs,
        Pose productionCameraFromBoard,
        Pose worldCameraFromBoard,
        long productionBaselineTimestampNs = 0L,
        long resolvedWorldBaselineTimestampNs = 0L,
        bool historicalBaselineResolved = false,
        float worldBaselineHistoryAgeMs = 0f,
        int worldBaselineHistoryCount = 0,
        string baselineResolveFailureReason = "")
    {
        if (telemetryTimestampNs <= 0L ||
            !IsFinite(productionCameraFromBoard) ||
            !IsFinite(worldCameraFromBoard))
        {
            return false;
        }

        var translationResidualM = Vector3.Distance(
            productionCameraFromBoard.position,
            worldCameraFromBoard.position);
        var rotationResidualDeg = Quaternion.Angle(
            productionCameraFromBoard.rotation,
            worldCameraFromBoard.rotation);
        Snapshot = new WorldRotationPropagationEquivalenceSnapshot(
            true,
            telemetryTimestampNs,
            Snapshot.sampleCount + 1,
            translationResidualM,
            rotationResidualDeg,
            Mathf.Max(Snapshot.maxTranslationResidualM, translationResidualM),
            Mathf.Max(Snapshot.maxRotationResidualDeg, rotationResidualDeg),
            productionBaselineTimestampNs,
            resolvedWorldBaselineTimestampNs,
            historicalBaselineResolved,
            worldBaselineHistoryAgeMs,
            worldBaselineHistoryCount,
            baselineResolveFailureReason);
        return true;
    }

    private static bool IsFinite(Pose pose) =>
        float.IsFinite(pose.position.x) &&
        float.IsFinite(pose.position.y) &&
        float.IsFinite(pose.position.z) &&
        float.IsFinite(pose.rotation.x) &&
        float.IsFinite(pose.rotation.y) &&
        float.IsFinite(pose.rotation.z) &&
        float.IsFinite(pose.rotation.w);
}
