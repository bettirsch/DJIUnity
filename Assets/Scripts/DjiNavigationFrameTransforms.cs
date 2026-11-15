using UnityEngine;

/// <summary>
/// Rigid transforms for the DJI local navigation frame. Matrices named
/// parentFromChild map child-frame coordinates into parent-frame coordinates.
/// The navigation frame is local NED: +X north, +Y east, +Z down.
/// It is intentionally separate from Unity's ARCore world frame.
/// </summary>
public static class DjiNavigationFrameTransforms
{
    /// <summary>
    /// T_gimbal_camera for the declared camera optical frame: +X image right,
    /// +Y image down, +Z optical forward. At neutral attitude the DJI gimbal
    /// frame uses +X forward, +Y right, +Z down, so the camera basis columns
    /// in gimbal coordinates are (right, down, forward) = (+Y, +Z, +X).
    /// This is a coordinate-definition transform, not an empirically measured
    /// Mini 3 Pro optical-center offset.
    /// </summary>
    public static Quaternion DefaultGimbalFromOpenCvCameraRotation =>
        new Quaternion(0.5f, 0.5f, 0.5f, 0.5f);

    public static Matrix4x4 MatrixFromPose(Pose parentFromChild) =>
        Matrix4x4.TRS(parentFromChild.position, parentFromChild.rotation, Vector3.one);

    public static Pose PoseFromMatrix(Matrix4x4 parentFromChild)
    {
        var translation = parentFromChild.GetColumn(3);
        return new Pose(new Vector3(translation.x, translation.y, translation.z), parentFromChild.rotation);
    }

    public static Matrix4x4 NavigationFromAircraft(Pose navigationFromAircraft) =>
        MatrixFromPose(navigationFromAircraft);

    public static Matrix4x4 AircraftFromGimbal(Pose aircraftFromGimbal) =>
        MatrixFromPose(aircraftFromGimbal);

    public static Matrix4x4 GimbalFromCamera(Pose gimbalFromCamera) =>
        MatrixFromPose(gimbalFromCamera);

    public static Matrix4x4 NavigationFromCamera(
        Matrix4x4 navigationFromAircraft,
        Matrix4x4 aircraftFromGimbal,
        Matrix4x4 gimbalFromCamera) =>
        navigationFromAircraft * aircraftFromGimbal * gimbalFromCamera;

    /// <summary>
    /// DJI attitude is represented as intrinsic roll, pitch, yaw about body
    /// X (forward), Y (right), Z (down) respectively. This returns the
    /// equivalent body-to-parent rotation Rz(yaw) * Ry(pitch) * Rx(roll).
    /// </summary>
    public static Quaternion RotationFromDjiNedAttitudeDegrees(float pitch, float roll, float yaw)
    {
        var yawAboutNavigationDown = Quaternion.AngleAxis(yaw, Vector3.forward);
        var pitchAboutLateralAxis = Quaternion.AngleAxis(pitch, Vector3.up);
        var rollAboutLongitudinalAxis = Quaternion.AngleAxis(roll, Vector3.right);
        return Quaternion.Normalize(yawAboutNavigationDown * pitchAboutLateralAxis * rollAboutLongitudinalAxis);
    }
}
