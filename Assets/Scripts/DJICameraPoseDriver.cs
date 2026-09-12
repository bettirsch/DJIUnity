using System;
using UnityEngine;

public sealed class DJICameraPoseDriver : MonoBehaviour
{
    [Header("Polling")]
    [SerializeField] [Min(0.02f)] private float pollInterval = 0.1f;
    [SerializeField] private bool verboseLogs;
    [SerializeField] [Min(0.1f)] private float verboseLogInterval = 1f;

    [Header("Transform")]
    [SerializeField] private bool applyPosition = true;
    [SerializeField] private bool applyRotation = true;
    [SerializeField] [Min(0.001f)] private float worldScale = 1f;

    [Header("Orientation Tuning")]
    [SerializeField] private float pitchMultiplier = -1f;
    [SerializeField] private float yawMultiplier = 1f;
    [SerializeField] private float rollMultiplier = -1f;
    [SerializeField] private float yawOffsetDegrees;

    [Header("World Origin")]
    [SerializeField] private bool lockOriginToFirstPose = true;
    [SerializeField] private bool estimateGroundPlaneFromRelativeAltitude = true;

    public bool HasValidPose => _hasValidPose;
    public DJIPoseSnapshot Snapshot => _snapshot;
    public Vector3 AircraftWorldPosition => _aircraftWorldPosition;
    public Quaternion CameraWorldRotation => _cameraWorldRotation;
    public bool HasGroundPlaneEstimate => _originInitialized && _originHasLocation && estimateGroundPlaneFromRelativeAltitude;
    public float GroundPlaneWorldY => _groundPlaneWorldY;

    private bool _originInitialized;
    private bool _originHasLocation;
    private bool _hasValidPose;
    private float _nextPollAt;
    private GeodeticOrigin _origin;
    private DJIPoseSnapshot _snapshot;
    private Vector3 _aircraftWorldPosition;
    private Quaternion _cameraWorldRotation = Quaternion.identity;
    private float _groundPlaneWorldY;
    private float _nextVerboseLogAt;
    private float _nextVerboseRotationLogAt;
    private string _lastVerbosePoseStatus;

    private void Update()
    {
        if (Time.unscaledTime < _nextPollAt)
            return;

        _nextPollAt = Time.unscaledTime + pollInterval;
        RefreshPose();
    }

    private void RefreshPose()
    {
        var json = DJIPoseProvider.GetLatestPoseJson();
        if (string.IsNullOrWhiteSpace(json))
        {
            _hasValidPose = false;
            LogPoseStatus("Pose JSON unavailable; Android bridge call returned empty.");
            return;
        }

        DJIPoseSnapshot snapshot;
        try
        {
            snapshot = JsonUtility.FromJson<DJIPoseSnapshot>(json);
        }
        catch (Exception e)
        {
            if (verboseLogs)
                Debug.LogWarning("[DJI] Failed to parse pose JSON: " + e.Message);
            return;
        }

        if (snapshot == null)
        {
            _hasValidPose = false;
            LogPoseStatus("Pose JSON parsed to null snapshot.");
            return;
        }

        _snapshot = snapshot;
        var hasAircraftAltitude = snapshot.aircraft.hasAltitude;
        var hasAircraftPosition = snapshot.aircraft.hasLocation && hasAircraftAltitude;
        var hasCameraRotation = CanBuildCameraRotation(snapshot);
        _hasValidPose = hasAircraftPosition && hasCameraRotation;

        if (!_hasValidPose)
            LogPoseStatus(BuildPoseStatus(snapshot, hasAircraftPosition, hasCameraRotation));

        if (hasAircraftAltitude && (!_originInitialized || (hasAircraftPosition && !_originHasLocation)))
        {
            var originLatitude = snapshot.aircraft.hasLocation ? snapshot.aircraft.latitude : 0.0;
            var originLongitude = snapshot.aircraft.hasLocation ? snapshot.aircraft.longitude : 0.0;

            _origin = new GeodeticOrigin(
                originLatitude,
                originLongitude,
                snapshot.aircraft.altitude
            );
            _groundPlaneWorldY = estimateGroundPlaneFromRelativeAltitude
                ? (float)(-_origin.altitude * worldScale)
                : 0f;
            _originInitialized = true;
            _originHasLocation = snapshot.aircraft.hasLocation;

            if (verboseLogs)
                Debug.Log(
                    $"[DJI] Pose origin locked lat={_origin.latitude:F7} lon={_origin.longitude:F7} alt={_origin.altitude:F2} groundY={_groundPlaneWorldY:F2} hasLocation={snapshot.aircraft.hasLocation} hasFullPose={_hasValidPose} lockOrigin={lockOriginToFirstPose}"
                );
        }

        if (hasAircraftPosition && _originHasLocation)
        {
            _aircraftWorldPosition = GeoToUnityWorld(_origin, snapshot.aircraft, worldScale);

            if (applyPosition)
                transform.position = _aircraftWorldPosition;
        }

        if (hasCameraRotation)
        {
            _cameraWorldRotation = BuildCameraRotation(snapshot);

            if (applyRotation)
                transform.rotation = _cameraWorldRotation;

            LogRotationSnapshot(snapshot, _cameraWorldRotation);
        }
    }

    private static bool CanBuildCameraRotation(DJIPoseSnapshot snapshot)
    {
        if (snapshot == null || !snapshot.gimbal.hasAttitude)
            return false;

        if (snapshot.gimbal.hasYawRelativeToAircraftHeading)
            return snapshot.aircraft.hasAttitude;

        return true;
    }

    private void LogPoseStatus(string status)
    {
        if (!verboseLogs)
            return;

        if (Time.unscaledTime < _nextVerboseLogAt && status == _lastVerbosePoseStatus)
            return;

        _nextVerboseLogAt = Time.unscaledTime + Mathf.Max(0.1f, verboseLogInterval);
        _lastVerbosePoseStatus = status;
        Debug.Log($"[DJI] Pose status: {status}");
    }

    private void LogRotationSnapshot(DJIPoseSnapshot snapshot, Quaternion cameraRotation)
    {
        if (!verboseLogs || Time.unscaledTime < _nextVerboseRotationLogAt)
            return;

        _nextVerboseRotationLogAt = Time.unscaledTime + Mathf.Max(0.1f, verboseLogInterval);

        var euler = cameraRotation.eulerAngles;
        Debug.Log(
            $"[DJI] Pose rotation raw aircraft(p={snapshot.aircraft.pitch:F2}, r={snapshot.aircraft.roll:F2}, y={snapshot.aircraft.yaw:F2}) " +
            $"gimbal(p={snapshot.gimbal.pitch:F2}, r={snapshot.gimbal.roll:F2}, y={snapshot.gimbal.yaw:F2}, relY={snapshot.gimbal.yawRelativeToAircraftHeading:F2}) " +
            $"unityEuler(p={NormalizeAngle(euler.x):F2}, y={NormalizeAngle(euler.y):F2}, r={NormalizeAngle(euler.z):F2})"
        );
    }

    private static string BuildPoseStatus(
        DJIPoseSnapshot snapshot,
        bool hasAircraftPosition,
        bool hasCameraRotation
    )
    {
        if (snapshot == null)
            return "snapshot=null";

        return
            $"sdkReady={snapshot.sdkReady} hasPose={snapshot.hasPose} " +
            $"aircraftPosition={hasAircraftPosition} cameraRotation={hasCameraRotation} " +
            $"aircraft(location={snapshot.aircraft.hasLocation}, altitude={snapshot.aircraft.hasAltitude}, attitude={snapshot.aircraft.hasAttitude}) " +
            $"gimbal(attitude={snapshot.gimbal.hasAttitude}, yawRelative={snapshot.gimbal.hasYawRelativeToAircraftHeading})";
    }

    private Quaternion BuildCameraRotation(DJIPoseSnapshot snapshot)
    {
        var yaw = snapshot.gimbal.hasYawRelativeToAircraftHeading
            ? (float)(snapshot.aircraft.yaw + snapshot.gimbal.yawRelativeToAircraftHeading)
            : (float)snapshot.gimbal.yaw;

        var pitch = (float)snapshot.gimbal.pitch;
        var roll = (float)snapshot.gimbal.roll;

        return Quaternion.Euler(
            pitch * pitchMultiplier,
            yaw * yawMultiplier + yawOffsetDegrees,
            roll * rollMultiplier
        );
    }

    private static float NormalizeAngle(float angle)
    {
        while (angle > 180f)
            angle -= 360f;

        while (angle < -180f)
            angle += 360f;

        return angle;
    }

    private static Vector3 GeoToUnityWorld(GeodeticOrigin origin, DJIPoseSnapshot.AircraftPose aircraft, float scale)
    {
        const double EarthRadiusMeters = 6378137.0;

        var lat0Rad = origin.latitude * Mathf.Deg2Rad;
        var latRad = aircraft.latitude * Mathf.Deg2Rad;
        var lonRad = aircraft.longitude * Mathf.Deg2Rad;
        var lon0Rad = origin.longitude * Mathf.Deg2Rad;

        var dLat = latRad - lat0Rad;
        var dLon = lonRad - lon0Rad;
        var meanLat = 0.5 * (latRad + lat0Rad);

        var eastMeters = dLon * Math.Cos(meanLat) * EarthRadiusMeters;
        var northMeters = dLat * EarthRadiusMeters;
        var upMeters = aircraft.altitude - origin.altitude;

        return new Vector3(
            (float)eastMeters * scale,
            (float)upMeters * scale,
            (float)northMeters * scale
        );
    }

    [Serializable]
    private struct GeodeticOrigin
    {
        public double latitude;
        public double longitude;
        public double altitude;

        public GeodeticOrigin(double latitude, double longitude, double altitude)
        {
            this.latitude = latitude;
            this.longitude = longitude;
            this.altitude = altitude;
        }
    }
}
