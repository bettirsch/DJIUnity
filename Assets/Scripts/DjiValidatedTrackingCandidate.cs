using UnityEngine;

/// <summary>Required visual-source and orientation safety policy for every player build.</summary>
public static class DjiValidatedTrackingCandidate
{
    public static bool Enabled => true;

    // The existing visual render freshness budget is two seconds. Delivery
    // must not rejuvenate a source frame that has already exceeded it.
    public static bool IsFreshPositiveSource(long sourceNs, long arrivalNs, long acceptedNs) =>
        sourceNs > 0 && arrivalNs >= sourceNs && sourceNs > acceptedNs &&
        arrivalNs - sourceNs <= 2_000_000_000L;

    public static bool IsRotationConsistent(Pose predicted, Pose visual, float maximumDegrees, out float angle)
    {
        angle = Quaternion.Angle(predicted.rotation, visual.rotation);
        return IsFiniteRotation(predicted.rotation) && IsFiniteRotation(visual.rotation) &&
               float.IsFinite(angle) && angle <= maximumDegrees;
    }

    public static bool IsFiniteRotation(Quaternion q) => float.IsFinite(q.x) && float.IsFinite(q.y) &&
        float.IsFinite(q.z) && float.IsFinite(q.w) && Quaternion.Dot(q, q) > 0f;
}
