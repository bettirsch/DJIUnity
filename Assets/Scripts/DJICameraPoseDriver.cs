using System;
using UnityEngine;

public sealed class DJICameraPoseDriver : MonoBehaviour
{
    [Header("Polling")]
    [SerializeField] [Min(0.02f)] private float pollInterval = 0.1f;
    [SerializeField] private bool verboseLogs;

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
    public bool HasGroundPlaneEstimate => _originInitialized && estimateGroundPlaneFromRelativeAltitude;
    public float GroundPlaneWorldY => _groundPlaneWorldY;

    private bool _originInitialized;
    private bool _hasValidPose;
    private float _nextPollAt;
    private GeodeticOrigin _origin;
    private DJIPoseSnapshot _snapshot;
    private Vector3 _aircraftWorldPosition;
    private Quaternion _cameraWorldRotation = Quaternion.identity;
    private float _groundPlaneWorldY;

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
            return;

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
            return;
        }

        _snapshot = snapshot;
        var hasAircraftPosition = snapshot.aircraft.hasLocation && snapshot.aircraft.hasAltitude;
        var hasCameraRotation = CanBuildCameraRotation(snapshot);
        _hasValidPose = hasAircraftPosition && hasCameraRotation;

        if (!hasAircraftPosition)
            return;

        if (!_originInitialized)
        {
            _origin = new GeodeticOrigin(
                snapshot.aircraft.latitude,
                snapshot.aircraft.longitude,
                snapshot.aircraft.altitude
            );
            _groundPlaneWorldY = estimateGroundPlaneFromRelativeAltitude
                ? (float)(-_origin.altitude * worldScale)
                : 0f;
            _originInitialized = true;

            if (verboseLogs)
                Debug.Log(
                    $"[DJI] Pose origin locked lat={_origin.latitude:F7} lon={_origin.longitude:F7} alt={_origin.altitude:F2} groundY={_groundPlaneWorldY:F2} hasFullPose={_hasValidPose} lockOrigin={lockOriginToFirstPose}"
                );
        }

        _aircraftWorldPosition = GeoToUnityWorld(_origin, snapshot.aircraft, worldScale);

        if (applyPosition)
            transform.position = _aircraftWorldPosition;

        if (!hasCameraRotation)
            return;

        _cameraWorldRotation = BuildCameraRotation(snapshot);

        if (applyRotation)
            transform.rotation = _cameraWorldRotation;
    }

    private static bool CanBuildCameraRotation(DJIPoseSnapshot snapshot)
    {
        if (snapshot == null || !snapshot.gimbal.hasAttitude)
            return false;

        if (snapshot.gimbal.hasYawRelativeToAircraftHeading)
            return snapshot.aircraft.hasAttitude;

        return true;
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
