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
    [Tooltip("T_gimbal_camera. Calibrate this once when the physical Mini camera axes and optical-center offset are measured. Identity is an explicit unverified default, not an axis correction.")]
    [SerializeField] private Vector3 gimbalFromCameraPositionMeters = Vector3.zero;
    [SerializeField] private Quaternion gimbalFromCameraRotation = Quaternion.identity;

    [Header("Diagnostics")]
    [SerializeField] private bool diagnosticLogging;
    [SerializeField] [Min(0.1f)] private float diagnosticLogIntervalSeconds = 1f;

    public bool HasTelemetryPose { get; private set; }
    /// <summary>
    /// T_navigation_camera. Its Vector3 components use local NED coordinates,
    /// not Unity world coordinates. The camera local axes are DJI optical-frame
    /// +X forward, +Y right, +Z down until GimbalFromCamera is calibrated.
    /// </summary>
    public Pose CurrentNavigationCameraPose => DjiNavigationFrameTransforms.PoseFromMatrix(_navigationFromCamera);
    public Matrix4x4 NavigationFromAircraft { get; private set; } = Matrix4x4.identity;
    public Matrix4x4 AircraftFromGimbal { get; private set; } = Matrix4x4.identity;
    public Matrix4x4 GimbalFromCamera => DjiNavigationFrameTransforms.GimbalFromCamera(CurrentGimbalFromCameraCalibration);
    public Matrix4x4 NavigationFromCamera => _navigationFromCamera;
    public DJIPoseSnapshot LatestSnapshot { get; private set; }
    public Vector3 CameraForwardInNavigation => CurrentNavigationCameraPose.rotation * Vector3.right;
    public Vector3 CameraRightInNavigation => CurrentNavigationCameraPose.rotation * Vector3.up;
    public Vector3 CameraDownInNavigation => CurrentNavigationCameraPose.rotation * Vector3.forward;

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
            HasTelemetryPose = false;
            return;
        }

        DJIPoseSnapshot snapshot;
        try
        {
            snapshot = JsonUtility.FromJson<DJIPoseSnapshot>(json);
        }
        catch (Exception exception)
        {
            HasTelemetryPose = false;
            Debug.LogWarning($"DJI_CAMERA_TELEMETRY_PARSE_FAILED reason={exception.Message}");
            return;
        }

        if (snapshot == null)
        {
            HasTelemetryPose = false;
            return;
        }

        LatestSnapshot = snapshot;
        var hasPosition = snapshot.aircraft.hasLocation && snapshot.aircraft.hasAltitude;
        var hasAttitude = snapshot.aircraft.hasAttitude && snapshot.gimbal.hasAttitude;
        HasTelemetryPose = hasPosition && hasAttitude;
        if (!HasTelemetryPose)
            return;

        if (!_hasNavigationOrigin)
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
            CalculateNavigationFromAircraftPosition(snapshot.aircraft),
            navigationFromAircraftRotation);
        NavigationFromAircraft = DjiNavigationFrameTransforms.NavigationFromAircraft(navigationFromAircraft);

        var aircraftFromGimbalRotation = BuildAircraftFromGimbalRotation(snapshot, navigationFromAircraftRotation);
        AircraftFromGimbal = DjiNavigationFrameTransforms.AircraftFromGimbal(
            new Pose(Vector3.zero, aircraftFromGimbalRotation));
        _navigationFromCamera = DjiNavigationFrameTransforms.NavigationFromCamera(
            NavigationFromAircraft,
            AircraftFromGimbal,
            GimbalFromCamera);

        LogDiagnostics(snapshot, navigationFromAircraftRotation, aircraftFromGimbalRotation);
        LogSeparateReferenceFrameOnce();
    }

    private Quaternion BuildAircraftFromGimbalRotation(
        DJIPoseSnapshot snapshot,
        Quaternion navigationFromAircraftRotation)
    {
        // DJI supplies an explicit gimbal yaw relative to aircraft heading. It
        // lets us keep the dynamic gimbal rotation in the aircraft frame.
        if (snapshot.gimbal.hasYawRelativeToAircraftHeading)
        {
            return DjiNavigationFrameTransforms.RotationFromDjiNedAttitudeDegrees(
                (float)snapshot.gimbal.pitch,
                (float)snapshot.gimbal.roll,
                (float)snapshot.gimbal.yawRelativeToAircraftHeading);
        }

        // The MSDK documents KeyGimbalAttitude yaw in NED. Convert that
        // absolute gimbal orientation back into the aircraft frame once.
        var navigationFromGimbalRotation = DjiNavigationFrameTransforms.RotationFromDjiNedAttitudeDegrees(
            (float)snapshot.gimbal.pitch,
            (float)snapshot.gimbal.roll,
            (float)snapshot.gimbal.yaw);
        return Quaternion.Normalize(Quaternion.Inverse(navigationFromAircraftRotation) * navigationFromGimbalRotation);
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
        Quaternion aircraftFromGimbalRotation)
    {
        if (!diagnosticLogging || Time.unscaledTime < _nextDiagnosticLogTime)
            return;

        _nextDiagnosticLogTime = Time.unscaledTime + diagnosticLogIntervalSeconds;
        var cameraPose = CurrentNavigationCameraPose;
        Debug.Log(
            $"DJI_AIRCRAFT_ATTITUDE pitchDeg={snapshot.aircraft.pitch:F2} rollDeg={snapshot.aircraft.roll:F2} yawDeg={snapshot.aircraft.yaw:F2} " +
            $"navigationRotation={FormatQuaternion(navigationFromAircraftRotation)} positionNedMeters={FormatVector(NavigationFromAircraft.GetColumn(3))}");
        Debug.Log(
            $"DJI_GIMBAL_ATTITUDE pitchDeg={snapshot.gimbal.pitch:F2} rollDeg={snapshot.gimbal.roll:F2} yawDeg={snapshot.gimbal.yaw:F2} " +
            $"yawRelativeToAircraftDeg={snapshot.gimbal.yawRelativeToAircraftHeading:F2} relativeYawAvailable={snapshot.gimbal.hasYawRelativeToAircraftHeading} " +
            $"aircraftFromGimbalRotation={FormatQuaternion(aircraftFromGimbalRotation)}");
        Debug.Log(
            $"DJI_CAMERA_TELEMETRY_ROTATION navigationRotation={FormatQuaternion(cameraPose.rotation)} " +
            $"cameraForwardNed={FormatVector(CameraForwardInNavigation)} cameraRightNed={FormatVector(CameraRightInNavigation)} cameraDownNed={FormatVector(CameraDownInNavigation)} " +
            $"gimbalFromCameraCalibration={FormatQuaternion(CurrentGimbalFromCameraCalibration.rotation)} worldBoardAlignment=NOT_AVAILABLE");
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

        var yawEastForward = DjiNavigationFrameTransforms.RotationFromDjiNedAttitudeDegrees(0f, 0f, 90f) * Vector3.right;
        var gimbalDownForward = DjiNavigationFrameTransforms.RotationFromDjiNedAttitudeDegrees(-90f, 0f, 0f) * Vector3.right;
        Debug.Log(
            $"DJI_TELEMETRY_CONVENTION_CHECK yawPlus90ForwardNed={FormatVector(yawEastForward)} expected=(0,1,0) " +
            $"gimbalPitchMinus90ForwardNed={FormatVector(gimbalDownForward)} expected=(0,0,1)");
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
