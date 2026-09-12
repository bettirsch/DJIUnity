using System;
using UnityEngine;

/// <summary>
/// Builds T_navigation_camera from DJI telemetry only. Navigation is a local
/// NED frame (+X north, +Y east, +Z down) initialized at the first complete
/// GPS sample. It is not an ARCore/Unity world frame and is never combined
/// with PersistentReferenceFrame.WorldFromBoard in this component.
/// </summary>
[DisallowMultipleComponent]
public sealed class DjiCameraPoseProvider : MonoBehaviour
{
    private const double EarthRadiusMeters = 6378137.0;

    [Header("Telemetry Polling")]
    [SerializeField] [Min(0.02f)] private float pollIntervalSeconds = 0.1f;

    [Header("Fixed Physical Camera Extrinsic")]
    [Tooltip("T_gimbal_camera. The default converts the declared OpenCV optical camera axes to neutral DJI gimbal axes. Its zero translation is an unverified optical-center offset and must be measured before production localization.")]
    [SerializeField] private Vector3 gimbalFromCameraPositionMeters = Vector3.zero;
    [SerializeField] private Quaternion gimbalFromCameraRotation = new Quaternion(0.5f, 0.5f, 0.5f, 0.5f);

    [Header("Diagnostics")]
    [SerializeField] private bool diagnosticLogging = true;
    [SerializeField] [Min(0.1f)] private float diagnosticLogIntervalSeconds = 1f;

    public bool HasTelemetryPose => HasTelemetryOrientation && HasTelemetryPosition;
    public bool HasTelemetryOrientation { get; private set; }
    public bool HasTelemetryPosition { get; private set; }
    /// <summary>
    /// T_navigation_camera. Its Vector3 components use local NED coordinates,
    /// not Unity world coordinates. Its position is valid only when
    /// HasTelemetryPosition is true. Camera axes use the OpenCV optical-frame
    /// convention: +X image right, +Y image down, +Z optical forward.
    /// </summary>
    public Pose CurrentNavigationCameraPose => DjiNavigationFrameTransforms.PoseFromMatrix(_navigationFromCamera);
    public Matrix4x4 NavigationFromAircraft { get; private set; } = Matrix4x4.identity;
    public Matrix4x4 AircraftFromGimbal { get; private set; } = Matrix4x4.identity;
    public Matrix4x4 GimbalFromCamera => DjiNavigationFrameTransforms.GimbalFromCamera(CurrentGimbalFromCameraCalibration);
    public Matrix4x4 NavigationFromCamera => _navigationFromCamera;
    public DJIPoseSnapshot LatestSnapshot { get; private set; }
    public Vector3 CameraForwardInNavigation => CurrentNavigationCameraPose.rotation * Vector3.forward;
    public Vector3 CameraRightInNavigation => CurrentNavigationCameraPose.rotation * Vector3.right;
    public Vector3 CameraDownInNavigation => CurrentNavigationCameraPose.rotation * Vector3.up;
    public Vector3 CameraUpInNavigation => -CameraDownInNavigation;

    private Matrix4x4 _navigationFromCamera = Matrix4x4.identity;
    private bool _hasNavigationOrigin;
    private GeodeticNavigationOrigin _navigationOrigin;
    private float _nextPollTime;
    private float _nextDiagnosticLogTime;
    private bool _separateReferenceFrameLogged;

    public Pose CurrentGimbalFromCameraCalibration => new Pose(
        gimbalFromCameraPositionMeters,
        Quaternion.Normalize(gimbalFromCameraRotation));

    /// <summary>
    /// Replaces the one fixed T_gimbal_camera calibration. This must be set
    /// from measured camera mounting data, never from visually tuned offsets.
    /// </summary>
    public void SetGimbalFromCameraCalibration(Pose gimbalFromCamera)
    {
        gimbalFromCameraPositionMeters = gimbalFromCamera.position;
        gimbalFromCameraRotation = Quaternion.Normalize(gimbalFromCamera.rotation);
    }

    public void ConfigureDiagnosticLogging(bool enabled, float intervalSeconds = 1f)
    {
        diagnosticLogging = enabled;
        diagnosticLogIntervalSeconds = Mathf.Max(0.1f, intervalSeconds);
    }

    private void Awake()
    {
        gimbalFromCameraRotation = Quaternion.Normalize(gimbalFromCameraRotation);
        Debug.Log("DJI_CAMERA_TELEMETRY_PROVIDER_READY navigationFrame=local_NED worldBoardAlignment=NOT_AVAILABLE");
        LogConventionChecks();
    }

    private void Update()
    {
        if (Time.unscaledTime < _nextPollTime)
            return;

        _nextPollTime = Time.unscaledTime + pollIntervalSeconds;
        RefreshTelemetryPose();
    }

    private void RefreshTelemetryPose()
    {
        var json = DJIPoseProvider.GetLatestPoseJson();
        if (string.IsNullOrWhiteSpace(json))
        {
            HasTelemetryOrientation = false;
            HasTelemetryPosition = false;
            return;
        }

        DJIPoseSnapshot snapshot;
        try
        {
            snapshot = JsonUtility.FromJson<DJIPoseSnapshot>(json);
        }
        catch (Exception exception)
        {
            HasTelemetryOrientation = false;
            HasTelemetryPosition = false;
            Debug.LogWarning($"DJI_CAMERA_TELEMETRY_PARSE_FAILED reason={exception.Message}");
            return;
        }

        if (snapshot == null)
        {
            HasTelemetryOrientation = false;
            HasTelemetryPosition = false;
            return;
        }

        LatestSnapshot = snapshot;
        HasTelemetryPosition = snapshot.aircraft.hasLocation && snapshot.aircraft.hasAltitude;
        HasTelemetryOrientation = snapshot.aircraft.hasAttitude && snapshot.gimbal.hasAttitude;
        if (!HasTelemetryOrientation)
            return;

        if (HasTelemetryPosition && !_hasNavigationOrigin)
        {
            _navigationOrigin = new GeodeticNavigationOrigin(
                snapshot.aircraft.latitude,
                snapshot.aircraft.longitude,
                snapshot.aircraft.altitude);
            _hasNavigationOrigin = true;
            Debug.Log($"DJI_NAVIGATION_ORIGIN_LOCKED latitude={_navigationOrigin.latitude:F7} longitude={_navigationOrigin.longitude:F7} altitudeMeters={_navigationOrigin.altitudeMeters:F2}");
        }

        var navigationFromAircraftRotation = DjiNavigationFrameTransforms.RotationFromDjiNedAttitudeDegrees(
            (float)snapshot.aircraft.pitch,
            (float)snapshot.aircraft.roll,
            (float)snapshot.aircraft.yaw);
        var navigationFromAircraft = new Pose(
            HasTelemetryPosition
                ? CalculateNavigationFromAircraftPosition(snapshot.aircraft)
                : Vector3.zero,
            navigationFromAircraftRotation);
        NavigationFromAircraft = DjiNavigationFrameTransforms.NavigationFromAircraft(navigationFromAircraft);

        var navigationFromGimbalRotation = BuildNavigationFromGimbalRotation(snapshot);
        var aircraftFromGimbalRotation = Quaternion.Normalize(
            Quaternion.Inverse(navigationFromAircraftRotation) * navigationFromGimbalRotation);
        AircraftFromGimbal = DjiNavigationFrameTransforms.AircraftFromGimbal(
            new Pose(Vector3.zero, aircraftFromGimbalRotation));
        _navigationFromCamera = DjiNavigationFrameTransforms.NavigationFromCamera(
            NavigationFromAircraft,
            AircraftFromGimbal,
            GimbalFromCamera);

        LogDiagnostics(
            snapshot,
            navigationFromAircraftRotation,
            aircraftFromGimbalRotation,
            navigationFromGimbalRotation);
        LogSeparateReferenceFrameOnce();
    }

    private static Quaternion BuildNavigationFromGimbalRotation(DJIPoseSnapshot snapshot)
    {
        // The physical-device telemetry shows that KeyGimbalAttitude remains
        // near level while aircraft pitch/roll change. Treat the complete
        // attitude as navigation-frame orientation. The separate relative-yaw
        // key is retained only as a diagnostic cross-check, never composed.
        return DjiNavigationFrameTransforms.RotationFromDjiNedAttitudeDegrees(
            (float)snapshot.gimbal.pitch,
            (float)snapshot.gimbal.roll,
            (float)snapshot.gimbal.yaw);
    }

    private Vector3 CalculateNavigationFromAircraftPosition(DJIPoseSnapshot.AircraftPose aircraft)
    {
        var originLatitudeRadians = _navigationOrigin.latitude * Mathf.Deg2Rad;
        var latitudeRadians = aircraft.latitude * Mathf.Deg2Rad;
        var longitudeRadians = aircraft.longitude * Mathf.Deg2Rad;
        var originLongitudeRadians = _navigationOrigin.longitude * Mathf.Deg2Rad;
        var northMeters = (latitudeRadians - originLatitudeRadians) * EarthRadiusMeters;
        var eastMeters = (longitudeRadians - originLongitudeRadians) * Math.Cos(0.5 * (latitudeRadians + originLatitudeRadians)) * EarthRadiusMeters;
        var downMeters = _navigationOrigin.altitudeMeters - aircraft.altitude;
        return new Vector3((float)northMeters, (float)eastMeters, (float)downMeters);
    }

    private void LogDiagnostics(
        DJIPoseSnapshot snapshot,
        Quaternion navigationFromAircraftRotation,
        Quaternion aircraftFromGimbalRotation,
        Quaternion navigationFromGimbalRotation)
    {
        if (!diagnosticLogging || Time.unscaledTime < _nextDiagnosticLogTime)
            return;

        _nextDiagnosticLogTime = Time.unscaledTime + diagnosticLogIntervalSeconds;
        var cameraPose = CurrentNavigationCameraPose;
        var gimbalYawMinusAircraftDegrees = Mathf.DeltaAngle(
            (float)snapshot.aircraft.yaw,
            (float)snapshot.gimbal.yaw);
        var relativeYawErrorDegrees = snapshot.gimbal.hasYawRelativeToAircraftHeading
            ? Mathf.DeltaAngle((float)snapshot.gimbal.yawRelativeToAircraftHeading, gimbalYawMinusAircraftDegrees)
            : float.NaN;
        Debug.Log(
            $"DJI_POSE_RAW aircraftPitchDeg={snapshot.aircraft.pitch:F2} aircraftRollDeg={snapshot.aircraft.roll:F2} aircraftYawDeg={snapshot.aircraft.yaw:F2} " +
            $"gimbalPitchDeg={snapshot.gimbal.pitch:F2} gimbalRollDeg={snapshot.gimbal.roll:F2} gimbalYawDeg={snapshot.gimbal.yaw:F2} " +
            $"gimbalYawRelativeToAircraftDeg={snapshot.gimbal.yawRelativeToAircraftHeading:F2} relativeYawAvailable={snapshot.gimbal.hasYawRelativeToAircraftHeading} " +
            $"hasOrientation={HasTelemetryOrientation} hasPosition={HasTelemetryPosition}");
        Debug.Log(
            $"DJI_AIRCRAFT_ROTATION navigationFromAircraft={FormatQuaternion(navigationFromAircraftRotation)} " +
            $"positionNedMeters={(HasTelemetryPosition ? FormatVector(NavigationFromAircraft.GetColumn(3)) : "UNAVAILABLE")}");
        Debug.Log(
            $"DJI_GIMBAL_ROTATION aircraftFromGimbal={FormatQuaternion(aircraftFromGimbalRotation)} " +
            $"navigationFromGimbal={FormatQuaternion(navigationFromGimbalRotation)} gimbalYawMinusAircraftDeg={gimbalYawMinusAircraftDegrees:F2} " +
            $"relativeYawErrorDeg={relativeYawErrorDegrees:F2} yawSource=NED_ABSOLUTE_VALIDATED_BY_RELATIVE_YAW");
        Debug.Log(
            $"DJI_CAMERA_FORWARD navigationFromCamera={FormatQuaternion(cameraPose.rotation)} " +
            $"forwardNed={FormatVector(CameraForwardInNavigation)} upNed={FormatVector(CameraUpInNavigation)} rightNed={FormatVector(CameraRightInNavigation)} " +
            $"gimbalFromCamera={FormatQuaternion(CurrentGimbalFromCameraCalibration.rotation)} worldBoardAlignment=NOT_AVAILABLE");
    }

    private void LogSeparateReferenceFrameOnce()
    {
        if (_separateReferenceFrameLogged)
            return;

        _separateReferenceFrameLogged = true;
        var hasWorldBoard = PersistentReferenceFrame.TryGetExisting(out var referenceFrame) && referenceFrame.HasBoardPose;
        Debug.Log($"DJI_TELEMETRY_FRAME_SEPARATE persistentWorldBoardAvailable={hasWorldBoard} navigationToWorldAlignment=NOT_COMPUTED");
    }

    private void LogConventionChecks()
    {
        if (!diagnosticLogging)
            return;

        var gimbalFromCamera = DjiNavigationFrameTransforms.DefaultGimbalFromOpenCvCameraRotation;
        var yawEastForward = DjiNavigationFrameTransforms.RotationFromDjiNedAttitudeDegrees(0f, 0f, 90f) * gimbalFromCamera * Vector3.forward;
        var gimbalDownForward = DjiNavigationFrameTransforms.RotationFromDjiNedAttitudeDegrees(-90f, 0f, 0f) * gimbalFromCamera * Vector3.forward;
        Debug.Log(
            $"DJI_TELEMETRY_CONVENTION_CHECK yawPlus90ForwardNed={FormatVector(yawEastForward)} expected=(0,1,0) " +
            $"gimbalPitchMinus90CameraForwardNed={FormatVector(gimbalDownForward)} expected=(0,0,1)");
    }

    private static string FormatQuaternion(Quaternion rotation) =>
        $"({rotation.x:F4},{rotation.y:F4},{rotation.z:F4},{rotation.w:F4})";

    private static string FormatVector(Vector4 vector) =>
        $"({vector.x:F3},{vector.y:F3},{vector.z:F3})";

    private static string FormatVector(Vector3 vector) =>
        $"({vector.x:F3},{vector.y:F3},{vector.z:F3})";

    [Serializable]
    private struct GeodeticNavigationOrigin
    {
        public double latitude;
        public double longitude;
        public double altitudeMeters;

        public GeodeticNavigationOrigin(double latitude, double longitude, double altitudeMeters)
        {
            this.latitude = latitude;
            this.longitude = longitude;
            this.altitudeMeters = altitudeMeters;
        }
    }
}
