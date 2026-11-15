using UnityEngine;

/// <summary>
/// Extracts a signed yaw twist from a relative camera rotation without using
/// Euler angles. Navigation uses NED, so +Z is physical down; positive yaw is
/// the right-hand rotation about NED +Z (clockwise when viewed from above).
/// </summary>
public static class QuaternionYawValidation
{
    /// <summary>Physical vertical axis in local NED coordinates: +Z is down.</summary>
    public static readonly Vector3 NavigationYawAxis = Vector3.forward;

    /// <summary>
    /// Expresses the physical NED-down yaw axis in the current camera frame.
    /// Relative C1FromC0 rotations must be evaluated about this same axis.
    /// </summary>
    public static Vector3 CurrentCameraYawAxis(Quaternion navigationFromCurrentCamera) =>
        (Quaternion.Inverse(Quaternion.Normalize(navigationFromCurrentCamera)) * NavigationYawAxis).normalized;

    /// <summary>
    /// Returns the signed twist component of a relative rotation about axis.
    /// The result is in [-180, 180] degrees and is invariant to q versus -q.
    /// </summary>
    public static float SignedTwistDegrees(Quaternion relativeRotation, Vector3 axis)
    {
        axis = axis.normalized;
        var rotation = Quaternion.Normalize(relativeRotation);
        if (rotation.w < 0f)
            rotation = new Quaternion(-rotation.x, -rotation.y, -rotation.z, -rotation.w);

        var projected = new Vector3(rotation.x, rotation.y, rotation.z);
        var twistVector = axis * Vector3.Dot(projected, axis);
        var twistMagnitudeSquared = twistVector.sqrMagnitude + rotation.w * rotation.w;
        if (twistMagnitudeSquared < 1e-8f)
            return 0f;

        var inverseMagnitude = 1f / Mathf.Sqrt(twistMagnitudeSquared);
        var signedRadians = 2f * Mathf.Atan2(
            Vector3.Dot(twistVector, axis) * inverseMagnitude,
            rotation.w * inverseMagnitude);
        return Mathf.DeltaAngle(0f, signedRadians * Mathf.Rad2Deg);
    }

    public static float SignedYawDegrees(
        Quaternion camera1FromCamera0,
        Quaternion navigationFromCamera1) =>
        SignedTwistDegrees(camera1FromCamera0, CurrentCameraYawAxis(navigationFromCamera1));
}
