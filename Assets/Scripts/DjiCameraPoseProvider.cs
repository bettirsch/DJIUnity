using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

/// <summary>
/// Builds T_navigation_camera from DJI telemetry only. Navigation is a local
/// NED frame (+X north, +Y east, +Z down) initialized at the first complete
/// GPS sample. It is not an ARCore/Unity world frame and is never combined
/// with the navigation frame in this component.
/// </summary>
[DisallowMultipleComponent]
public sealed class DjiCameraPoseProvider : MonoBehaviour
{
    public readonly struct TelemetryOrientationSample
    {
        public readonly long timestampNs;
        public readonly Quaternion navigationFromCameraRotation;
        public readonly bool hasOrientation;
        // Raw DJI angles are retained only to label controlled diagnostics.
        // They never participate in pose composition or rendering.
        public readonly float aircraftYawDegrees;
        public readonly float gimbalPitchDegrees;
        public readonly OrientationTelemetryProvenance provenance;
        public readonly long calculationTimestampNs; // Diagnostic only; never a source/freshness timestamp.

        public TelemetryOrientationSample(
            long timestampNs,
            Quaternion navigationFromCameraRotation,
            bool hasOrientation,
            float aircraftYawDegrees,
            float gimbalPitchDegrees,
            OrientationTelemetryProvenance provenance = default,
            long calculationTimestampNs = 0L)
        {
            this.timestampNs = timestampNs;
            this.navigationFromCameraRotation = navigationFromCameraRotation;
            this.hasOrientation = hasOrientation;
            this.aircraftYawDegrees = aircraftYawDegrees;
            this.gimbalPitchDegrees = gimbalPitchDegrees;
            this.provenance = provenance;
            this.calculationTimestampNs = calculationTimestampNs;
        }
    }

    /// <summary>
    /// An owned KeyAircraftVelocity callback sample in DJI local NED. Its
    /// timestamp is callback arrival time, not an attitude or video timestamp.
    /// Velocity is currently diagnostic-only and unit validation is pending.
    /// </summary>
    public readonly struct TelemetryVelocitySample
    {
        public readonly long timestampNs;
        public readonly Vector3 navigationVelocityMetersPerSecond;

        public TelemetryVelocitySample(long timestampNs, Vector3 navigationVelocityMetersPerSecond)
        {
            this.timestampNs = timestampNs;
            this.navigationVelocityMetersPerSecond = navigationVelocityMetersPerSecond;
        }
    }

    public enum TelemetryPositionCapability
    {
        None,
        Partial,
        Full
    }

    private const double EarthRadiusMeters = 6378137.0;

    [Header("Telemetry Polling")]
    [SerializeField] [Min(0.02f)] private float pollIntervalSeconds = 0.1f;
    [SerializeField] [Min(1f)] private float maximumVelocityInterpolationBracketMilliseconds = 500f;

    [Header("Fixed Physical Camera Extrinsic")]
    [Tooltip("T_gimbal_camera. The default converts the declared OpenCV optical camera axes to neutral DJI gimbal axes. Its zero translation is an unverified optical-center offset and must be measured before production localization.")]
    [SerializeField] private Vector3 gimbalFromCameraPositionMeters = Vector3.zero;
    [SerializeField] private Quaternion gimbalFromCameraRotation = new Quaternion(0.5f, 0.5f, 0.5f, 0.5f);

    [Header("Diagnostics")]
    [SerializeField] private bool diagnosticLogging = true;
    [SerializeField] [Min(0.1f)] private float diagnosticLogIntervalSeconds = 1f;

    public bool HasTelemetryPose => HasTelemetryOrientation && HasTelemetryPosition;
    public bool HasTelemetryOrientation { get; private set; }
    /// <summary>
    /// Intentionally false during the position-source audit. Raw DJI values
    /// must not be treated as a navigation position before their source,
    /// validity, and reference semantics are physically confirmed.
    /// </summary>
    public bool HasTelemetryPosition { get; private set; }
    /// <summary>
    /// Position capability remains None until the physical audit proves a
    /// complete, valid horizontal and vertical source.
    /// </summary>
    public TelemetryPositionCapability PositionCapability => TelemetryPositionCapability.None;
    public long LatestTelemetryTimestampMs => LatestSnapshot?.timestampMs ?? 0L;
    public long LatestTelemetryArrivalTimestampNs => LatestSnapshot?.telemetryArrivalTimestampNs ?? 0L;
    public long LatestAircraftAttitudeArrivalTimestampNs => LatestSnapshot?.aircraftAttitudeArrivalTimestampNs ?? 0L;
    public long LatestGimbalAttitudeArrivalTimestampNs => LatestSnapshot?.gimbalAttitudeArrivalTimestampNs ?? 0L;
    public long LatestVelocityTimestampNs => LatestSnapshot?.positionAudit?.latestVelocityTimestampNs ?? 0L;
    public int LatestBridgeVelocityHistoryCount => LatestSnapshot?.positionAudit?.velocityHistoryCount ?? 0;
    public long LatestBridgeNewestVelocityHistoryTimestampNs =>
        LatestSnapshot?.positionAudit?.newestVelocityHistoryTimestampNs ?? 0L;
    /// <summary>
    /// Latest count supplied by the existing position-audit snapshot. This is
    /// informational only: a count does not imply that GNSS position is valid.
    /// </summary>
    public int? LatestGpsSatelliteCount { get; private set; }
    /// <summary>
    /// Latest aircraft smart-battery percentage from DJI telemetry. A null
    /// value means no valid callback has arrived; it is not equivalent to 0%.
    /// </summary>
    public int? LatestAircraftBatteryPercent { get; private set; }
    /// <summary>
    /// Returns only the magnitude of the audited DJI aircraft velocity. It is
    /// a visual-quality diagnostic bound, never a position source or a term in
    /// the rotation-only rendered pose.
    /// </summary>
    public bool TryGetAircraftVelocityMagnitudeMetersPerSecond(out float speedMetersPerSecond)
    {
        speedMetersPerSecond = 0f;
        var audit = LatestSnapshot?.positionAudit;
        if (audit == null || !audit.hasAircraftVelocity)
            return false;

        var velocity = new Vector3(
            audit.aircraftVelocityX,
            audit.aircraftVelocityY,
            audit.aircraftVelocityZ);
        if (!float.IsFinite(velocity.x) || !float.IsFinite(velocity.y) || !float.IsFinite(velocity.z))
            return false;

        speedMetersPerSecond = velocity.magnitude;
        return float.IsFinite(speedMetersPerSecond);
    }
    /// <summary>
    /// Bounded velocity samples in the same Android monotonic domain as the
    /// existing orientation history. They are not used by rendering.
    /// </summary>
    public IReadOnlyList<TelemetryVelocitySample> TelemetryVelocityHistory => _telemetryVelocityHistory.Samples;
    public int VelocityHistoryCount => _telemetryVelocityHistory.Count;
    public long NewestVelocityHistoryTimestampNs => _telemetryVelocityHistory.NewestTimestampNs;
    public long NewestTelemetryOrientationTimestampNs => GetNewestTelemetryHistoryTimestampNs();
    /// <summary>
    /// Timestamped KeyAircraftLocation3D samples for horizontal GNSS/local-NED
    /// diagnostics. They never enable HasTelemetryPosition or rendering.
    /// </summary>
    public IReadOnlyList<TelemetryAircraftPositionSample> AircraftPositionHistory => _aircraftPositionHistory.Samples;
    public int AircraftPositionHistoryCount => _aircraftPositionHistory.Count;
    public long NewestAircraftPositionTimestampNs => _aircraftPositionHistory.NewestTimestampNs;
    public bool TryGetVelocityAtTimestamp(
        long targetTimestampNs,
        out Vector3 navigationVelocityMetersPerSecond,
        out string reason)
    {
        return TryGetVelocityAtTimestamp(
            targetTimestampNs,
            (long)(maximumVelocityInterpolationBracketMilliseconds * 1_000_000d),
            out navigationVelocityMetersPerSecond,
            out reason);
    }

    /// <summary>
    /// Uses the existing bounded velocity history with an explicitly supplied
    /// diagnostic gap limit. It never extrapolates past available callbacks.
    /// </summary>
    public bool TryGetVelocityAtTimestamp(
        long targetTimestampNs,
        long maximumBracketGapNs,
        out Vector3 navigationVelocityMetersPerSecond,
        out string reason)
    {
        var found = _telemetryVelocityHistory.TryInterpolate(
            targetTimestampNs,
            maximumBracketGapNs,
            out navigationVelocityMetersPerSecond,
            out reason);
        if (!found)
            LogVelocityLookupFailureIfDue(targetTimestampNs, reason);
        return found;
    }
    /// <summary>
    /// Bounded, monotonic-clock orientation samples reserved for future
    /// vision/telemetry timing diagnostics. They are not used for rendering.
    /// </summary>
    public IReadOnlyList<TelemetryOrientationSample> TelemetryOrientationHistory => _telemetryOrientationHistory;
    /// <summary>
    /// Raised after an owned, monotonic-clock orientation sample is appended to
    /// the diagnostic history. Consumers must not use this event for rendering.
    /// </summary>
    public event Action<TelemetryOrientationSample> TelemetryOrientationRecorded;
    /// <summary>
    /// Raised after at least one new, timestamped velocity callback has been
    /// appended. Consumers use it only for diagnostic shadow calculations.
    /// </summary>
    public event Action<TelemetryVelocitySample> TelemetryVelocityRecorded;
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
    private const int MaximumTelemetryOrientationHistorySamples = 100;
    private readonly List<TelemetryOrientationSample> _telemetryOrientationHistory = new();
    private readonly TelemetryVelocityHistory _telemetryVelocityHistory = new(160);
    private readonly List<TelemetryVelocitySample> _bridgeVelocitySampleBuffer = new(160);
    private readonly TelemetryAircraftPositionHistory _aircraftPositionHistory = new(160);
    private readonly List<TelemetryAircraftPositionSample> _bridgeAircraftPositionSampleBuffer = new(160);
    private long _lastRecordedTelemetryTimestampNs;
    private bool _zeroTimestampHistoryRejectionLogged;
    private bool _nonMonotonicTimestampHistoryRejectionLogged;
    private bool _hasLoggedSatelliteSnapshot;
    private bool _lastLoggedSatelliteFieldPresent;
    private int? _lastLoggedSatelliteValue;
    private float _nextVelocityTimestampLogTime;
    private float _nextVelocityHistorySyncLogTime;
    private float _nextVelocityLookupFailureLogTime;
    private float _nextVelocityClockDomainLogTime;

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
        LatestGpsSatelliteCount = TryReadGpsSatelliteCount(snapshot.positionAudit);
        LatestAircraftBatteryPercent = TryReadAircraftBatteryPercent(snapshot.aircraftBattery);
        LogSatelliteSnapshotIfChanged(snapshot.positionAudit, LatestGpsSatelliteCount);
        RecordVelocityHistory(snapshot.positionAudit);
        RecordAircraftPositionHistory(snapshot.positionAudit);
        // Keep raw position data diagnostic-only. This prevents the existing
        // geodetic conversion path from treating an unclassified DJI source as
        // suitable for board-loss propagation.
        HasTelemetryPosition = false;
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
        RecordTelemetryOrientation(
            snapshot.telemetryArrivalTimestampNs,
            CurrentNavigationCameraPose.rotation,
            HasTelemetryOrientation,
            (float)snapshot.aircraft.yaw,
            (float)snapshot.gimbal.pitch);

        LogDiagnostics(
            snapshot,
            navigationFromAircraftRotation,
            aircraftFromGimbalRotation,
            navigationFromGimbalRotation);
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
        Debug.Log(
            "DJI_TELEMETRY_AUDIT " +
            $"aircraftAttitude=(pitch={snapshot.aircraft.pitch:F2}deg,roll={snapshot.aircraft.roll:F2}deg,yaw={snapshot.aircraft.yaw:F2}deg) " +
            $"gimbalAttitude=(pitch={snapshot.gimbal.pitch:F2}deg,roll={snapshot.gimbal.roll:F2}deg,yaw={snapshot.gimbal.yaw:F2}deg) " +
            $"latitude={(snapshot.aircraft.hasLocation ? snapshot.aircraft.latitude.ToString("F7") : "UNAVAILABLE")} " +
            $"longitude={(snapshot.aircraft.hasLocation ? snapshot.aircraft.longitude.ToString("F7") : "UNAVAILABLE")} " +
            $"altitude={(snapshot.aircraft.hasAltitude ? snapshot.aircraft.altitude.ToString("F2") + "m(KeyAltitude semantics=UNVERIFIED)" : "UNAVAILABLE")} " +
            "positionAudit=SEE_DJI_POSITION_AUDIT " +
            $"hasOrientation={HasTelemetryOrientation} hasPosition={HasTelemetryPosition} positionCapability={PositionCapability} " +
            $"NavigationFromCamera.position={FormatVector(cameraPose.position)} NavigationFromCamera.rotation={FormatQuaternion(cameraPose.rotation)} " +
            $"telemetryWallClockTimestampMs={LatestTelemetryTimestampMs}");
        LogPositionSourceAudit(snapshot);
        Debug.Log(
            $"DJI_TELEMETRY_TIMESTAMP timestampNs={LatestTelemetryArrivalTimestampNs} " +
            "source=SystemClock.elapsedRealtimeNanos_on_last_key_update clockDomain=ANDROID_MONOTONIC " +
            $"wallClockTimestampMs={LatestTelemetryTimestampMs} historyCount={_telemetryOrientationHistory.Count} " +
            $"newestHistoryTimestampNs={GetNewestTelemetryHistoryTimestampNs()}");
    }

    private static void LogPositionSourceAudit(DJIPoseSnapshot snapshot)
    {
        var aircraft = snapshot.aircraft ?? new DJIPoseSnapshot.AircraftPose();
        var audit = snapshot.positionAudit;
        audit ??= new DJIPoseSnapshot.PositionAudit();
        Debug.Log(
            "DJI_POSITION_AUDIT " +
            $"aircraftLocation3D={audit.aircraftLocation3DRaw} " +
            $"latitude={(aircraft.hasLocation ? aircraft.latitude.ToString("F7") : "UNAVAILABLE")} " +
            $"longitude={(aircraft.hasLocation ? aircraft.longitude.ToString("F7") : "UNAVAILABLE")} " +
            $"heightAboveSeaLevel={audit.heightAboveSeaLevelRaw} " +
            $"takeoffRelativePosition={audit.relativeToTakeoffPositionRaw} " +
            $"takeoffLocationAltitude={audit.takeoffLocationAltitudeRaw} " +
            $"aircraftVelocity={audit.aircraftVelocityRaw} " +
            $"gpsIsValid={audit.gpsIsValidRaw} gpsSignalLevel={audit.gpsSignalLevelRaw} " +
            $"gpsSatelliteCount={audit.gpsSatelliteCountRaw} homeLocation={audit.homeLocationRaw} " +
            $"horizontalValid={IsValidStatus(audit.aircraftLocation2DStatus)} verticalValid=false hasTelemetryPosition=false " +
            "takeoffLocationAltitudeSemantics=UNVALIDATED_FOR_AR_POSITION");
        LogPositionKeyAudit("KeyAircraftLocation", audit.aircraftLocation2DSupport, audit.aircraftLocation2DStatus, audit.aircraftLocation2DRaw);
        LogPositionKeyAudit("KeyAircraftLocation3D", audit.aircraftLocation3DSupport, audit.aircraftLocation3DStatus, audit.aircraftLocation3DRaw);
        LogPositionKeyAudit("KeyAltitude", audit.keyAltitudeSupport, audit.keyAltitudeStatus, audit.keyAltitudeRaw);
        LogPositionKeyAudit("KeyAircraftVelocity", audit.aircraftVelocitySupport, audit.aircraftVelocityStatus, audit.aircraftVelocityRaw);
        LogPositionKeyAudit("KeyGPSSatelliteCount", audit.gpsSatelliteCountSupport, audit.gpsSatelliteCountStatus, audit.gpsSatelliteCountRaw);
        LogPositionKeyAudit("KeyGPSSignalLevel", audit.gpsSignalLevelSupport, audit.gpsSignalLevelStatus, audit.gpsSignalLevelRaw);
        LogPositionKeyAudit("KeyGPSIsValid", audit.gpsIsValidSupport, audit.gpsIsValidStatus, audit.gpsIsValidRaw);
        LogPositionKeyAudit("KeyHomeLocation", audit.homeLocationSupport, audit.homeLocationStatus, audit.homeLocationRaw);
        LogPositionKeyAudit("KeyIsHomeLocationSet", audit.homeLocationSetSupport, audit.homeLocationSetStatus, audit.homeLocationSetRaw);
        LogPositionKeyAudit("KeyTakeoffLocationAltitude", audit.takeoffLocationAltitudeSupport, audit.takeoffLocationAltitudeStatus, audit.takeoffLocationAltitudeRaw);
        LogPositionKeyAudit("KeyHeightAboveSeaLevel", audit.heightAboveSeaLevelSupport, audit.heightAboveSeaLevelStatus, audit.heightAboveSeaLevelRaw);
        LogPositionKeyAudit("KeyRelateToTakeOffPosition", audit.relativeToTakeoffPositionSupport, audit.relativeToTakeoffPositionStatus, audit.relativeToTakeoffPositionRaw);
        Debug.Log(
            "DJI_POSITION_CAPABILITY_AUDIT " +
            $"POSITION_KEYS_SUPPORTED={AllAuditedKeysSupported(audit)} " +
            $"POSITION_DATA_CURRENTLY_VALID={HasCurrentlyValidPositionData(audit)} " +
            "TELEMETRY_TRANSLATION_CAPABILITY=NONE FUTURE_LOCAL_NED_POSSIBLE=UNVALIDATED");
    }

    private static void LogPositionKeyAudit(string key, bool supported, string valueStatus, string value) =>
        Debug.Log(
            $"DJI_POSITION_KEY_AUDIT key={key} support={(supported ? "KEY_SUPPORTED" : "KEY_UNSUPPORTED")} " +
            $"valueStatus={valueStatus ?? "KEY_NOT_SUBSCRIBED"} value={value ?? "null"}");

    private static bool IsValidStatus(string status) => status == "KEY_AVAILABLE_VALID";

    private void LogSatelliteSnapshotIfChanged(DJIPoseSnapshot.PositionAudit audit, int? satelliteCount)
    {
        audit ??= new DJIPoseSnapshot.PositionAudit();
        if (_hasLoggedSatelliteSnapshot &&
            _lastLoggedSatelliteFieldPresent == audit.hasGpsSatelliteCount &&
            _lastLoggedSatelliteValue == satelliteCount)
            return;

        _hasLoggedSatelliteSnapshot = true;
        _lastLoggedSatelliteFieldPresent = audit.hasGpsSatelliteCount;
        _lastLoggedSatelliteValue = satelliteCount;
        Debug.Log(
            $"DJI_SAT_UNITY_SNAPSHOT fieldPresent={audit.hasGpsSatelliteCount} " +
            $"value={audit.gpsSatelliteCountRaw ?? "null"} hasSatelliteCount={satelliteCount.HasValue} " +
            $"gpsIsValid={audit.gpsIsValidRaw ?? "null"} gpsSignalLevel={audit.gpsSignalLevelRaw ?? "null"}");
    }

    private static int? TryReadGpsSatelliteCount(DJIPoseSnapshot.PositionAudit audit)
    {
        if (audit == null || !audit.hasGpsSatelliteCount || string.IsNullOrWhiteSpace(audit.gpsSatelliteCountRaw))
            return null;

        return int.TryParse(
            audit.gpsSatelliteCountRaw,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var count) && count >= 0
            ? count
            : null;
    }

    private static int? TryReadAircraftBatteryPercent(DJIPoseSnapshot.AircraftBattery battery)
    {
        if (battery == null || !battery.hasPercent || battery.percent is < 0 or > 100)
            return null;

        return battery.percent;
    }

    private void RecordAircraftPositionHistory(DJIPoseSnapshot.PositionAudit audit)
    {
        if (audit?.aircraftPositionHistory == null || audit.aircraftPositionHistory.Length == 0)
            return;

        _bridgeAircraftPositionSampleBuffer.Clear();
        foreach (var sample in audit.aircraftPositionHistory)
        {
            if (sample == null)
                continue;
            _bridgeAircraftPositionSampleBuffer.Add(new TelemetryAircraftPositionSample(
                sample.timestampNs,
                sample.latitude,
                sample.longitude,
                sample.altitude));
        }

        _aircraftPositionHistory.SyncBridgeSamples(_bridgeAircraftPositionSampleBuffer);
    }

    private void RecordVelocityHistory(DJIPoseSnapshot.PositionAudit audit)
    {
        if (audit == null)
            return;

        _bridgeVelocitySampleBuffer.Clear();
        var bridgeSamples = audit.velocityHistory;
        if (bridgeSamples == null || bridgeSamples.Length == 0)
        {
            // Compatibility with a bridge snapshot that contains one explicit
            // timestamped sample but no history array. Never use attitude time.
            if (audit.hasAircraftVelocity && audit.latestVelocityTimestampNs > 0L)
            {
                _bridgeVelocitySampleBuffer.Add(new TelemetryVelocitySample(
                    audit.latestVelocityTimestampNs,
                    new Vector3(audit.aircraftVelocityX, audit.aircraftVelocityY, audit.aircraftVelocityZ)));
            }
        }
        else
        {
            foreach (var bridgeSample in bridgeSamples)
            {
                if (bridgeSample == null)
                    continue;

                _bridgeVelocitySampleBuffer.Add(new TelemetryVelocitySample(
                    bridgeSample.timestampNs,
                    new Vector3(
                        (float)bridgeSample.northMetersPerSecond,
                        (float)bridgeSample.eastMetersPerSecond,
                        (float)bridgeSample.downMetersPerSecond)));
            }
        }

        var sync = _telemetryVelocityHistory.SyncBridgeSamples(_bridgeVelocitySampleBuffer);
        LogVelocityHistorySyncIfDue(sync);
        if (sync.rejectedInvalid > 0)
        {
            Debug.LogWarning(
                $"DJI_VELOCITY_HISTORY_REJECTED reason={sync.firstRejectionReason} " +
                $"count={sync.rejectedInvalid} newestTimestampNs={NewestVelocityHistoryTimestampNs}");
        }

        if (sync.appendedNew > 0)
        {
            var newestVelocity = _telemetryVelocityHistory.Samples[_telemetryVelocityHistory.Count - 1];
            LogVelocityTimestampIfDue(newestVelocity);
            TelemetryVelocityRecorded?.Invoke(newestVelocity);
        }
    }

    private void LogVelocityHistorySyncIfDue(TelemetryVelocityHistory.SyncResult sync)
    {
        if (sync.received == 0 || Time.unscaledTime < _nextVelocityHistorySyncLogTime)
            return;

        _nextVelocityHistorySyncLogTime = Time.unscaledTime + 1f;
        Debug.Log(
            "DJI_VELOCITY_HISTORY_SYNC " +
            $"received={sync.received} skippedAlreadyKnown={sync.skippedAlreadyKnown} " +
            $"appendedNew={sync.appendedNew} unityHistoryCount={VelocityHistoryCount} " +
            $"newestTimestampNs={NewestVelocityHistoryTimestampNs}");
    }

    private void LogVelocityTimestampIfDue(TelemetryVelocitySample sample)
    {
        if (Time.unscaledTime < _nextVelocityTimestampLogTime)
            return;

        _nextVelocityTimestampLogTime = Time.unscaledTime + 1f;
        var velocity = sample.navigationVelocityMetersPerSecond;
        Debug.Log(
            "DJI_VELOCITY_TIMESTAMP " +
            $"timestampNs={sample.timestampNs} north={velocity.x:F3} east={velocity.y:F3} down={velocity.z:F3} " +
            $"speed={velocity.magnitude:F3} historyCount={VelocityHistoryCount} " +
            $"newestHistoryTimestampNs={NewestVelocityHistoryTimestampNs} " +
            "clockDomain=ANDROID_MONOTONIC VELOCITY_UNIT_VALIDATION=PENDING");
    }

    private void LogVelocityLookupFailureIfDue(long targetTimestampNs, string reason)
    {
        if (Time.unscaledTime < _nextVelocityLookupFailureLogTime)
            return;

        _nextVelocityLookupFailureLogTime = Time.unscaledTime + 1f;
        Debug.LogWarning(
            "DJI_VELOCITY_LOOKUP_FAILED " +
            $"reason={reason} targetTimestampNs={targetTimestampNs} " +
            $"oldestTimestampNs={_telemetryVelocityHistory.OldestTimestampNs} " +
            $"newestTimestampNs={NewestVelocityHistoryTimestampNs}");
    }

    /// <summary>
    /// Called by board-vision diagnostics only. It documents the common clock
    /// contract without feeding a pose into the renderer.
    /// </summary>
    public void LogVelocityClockDomainComparison(long videoFrameTimestampNs)
    {
        if (Time.unscaledTime < _nextVelocityClockDomainLogTime)
            return;

        _nextVelocityClockDomainLogTime = Time.unscaledTime + 1f;
        Debug.Log(
            "DJI_TELEMETRY_CLOCK_DOMAIN " +
            $"aircraftAttitudeTimestampNs={LatestAircraftAttitudeArrivalTimestampNs} " +
            $"gimbalTimestampNs={LatestGimbalAttitudeArrivalTimestampNs} " +
            $"velocityTimestampNs={LatestVelocityTimestampNs} " +
            $"videoFrameTimestampNs={videoFrameTimestampNs} " +
            "source=SystemClock.elapsedRealtimeNanos sameClockDomain=YES " +
            "VELOCITY_UNIT_VALIDATION=PENDING");
    }

    private static bool AllAuditedKeysSupported(DJIPoseSnapshot.PositionAudit audit) =>
        audit.aircraftLocation2DSupport && audit.aircraftLocation3DSupport && audit.keyAltitudeSupport &&
        audit.aircraftVelocitySupport && audit.gpsSatelliteCountSupport && audit.gpsSignalLevelSupport &&
        audit.gpsIsValidSupport && audit.homeLocationSupport && audit.homeLocationSetSupport &&
        audit.takeoffLocationAltitudeSupport && audit.heightAboveSeaLevelSupport && audit.relativeToTakeoffPositionSupport;

    private static bool HasCurrentlyValidPositionData(DJIPoseSnapshot.PositionAudit audit) =>
        IsValidStatus(audit.aircraftLocation3DStatus) &&
        IsValidStatus(audit.gpsIsValidStatus) &&
        IsValidStatus(audit.gpsSignalLevelStatus) &&
        IsValidStatus(audit.gpsSatelliteCountStatus) &&
        IsValidStatus(audit.heightAboveSeaLevelStatus);

    private void RecordTelemetryOrientation(
        long timestampNs,
        Quaternion navigationFromCameraRotation,
        bool hasOrientation,
        float aircraftYawDegrees,
        float gimbalPitchDegrees)
    {
        if (timestampNs <= 0)
        {
            if (!_zeroTimestampHistoryRejectionLogged)
            {
                _zeroTimestampHistoryRejectionLogged = true;
                Debug.LogWarning("DJI_TELEMETRY_HISTORY_REJECTED reason=ZERO_TIMESTAMP");
            }

            return;
        }

        if (!hasOrientation || timestampNs <= _lastRecordedTelemetryTimestampNs)
        {
            if (!_nonMonotonicTimestampHistoryRejectionLogged)
            {
                _nonMonotonicTimestampHistoryRejectionLogged = true;
                Debug.LogWarning(
                    $"DJI_TELEMETRY_HISTORY_REJECTED reason=NON_MONOTONIC_TIMESTAMP " +
                    $"timestampNs={timestampNs} previousTimestampNs={_lastRecordedTelemetryTimestampNs} " +
                    $"hasOrientation={hasOrientation}");
            }

            return;
        }

        _lastRecordedTelemetryTimestampNs = timestampNs;
        _zeroTimestampHistoryRejectionLogged = false;
        _nonMonotonicTimestampHistoryRejectionLogged = false;
        var sample = new TelemetryOrientationSample(
            timestampNs,
            navigationFromCameraRotation,
            hasOrientation,
            aircraftYawDegrees,
            gimbalPitchDegrees,
            new OrientationTelemetryProvenance(LatestSnapshot, NavigationFromAircraft.rotation,
                AircraftFromGimbal.rotation, CurrentGimbalFromCameraCalibration.rotation),
            DjiBoardVisionBridge.GetMonotonicTimestampNs());
        _telemetryOrientationHistory.Add(sample);
        if (_telemetryOrientationHistory.Count > MaximumTelemetryOrientationHistorySamples)
            _telemetryOrientationHistory.RemoveAt(0);

        TelemetryOrientationRecorded?.Invoke(sample);
    }

    private long GetNewestTelemetryHistoryTimestampNs() =>
        _telemetryOrientationHistory.Count == 0
            ? 0L
            : _telemetryOrientationHistory[_telemetryOrientationHistory.Count - 1].timestampNs;

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

/// <summary>
/// Pure bounded storage for NED velocity callback samples. It deliberately has
/// no rendering dependency so translation shadow work can validate lookup and
/// timing before it is ever considered for a camera pose.
/// </summary>
public sealed class TelemetryVelocityHistory
{
    public readonly struct SyncResult
    {
        public readonly int received;
        public readonly int skippedAlreadyKnown;
        public readonly int appendedNew;
        public readonly int rejectedInvalid;
        public readonly string firstRejectionReason;

        public SyncResult(
            int received,
            int skippedAlreadyKnown,
            int appendedNew,
            int rejectedInvalid,
            string firstRejectionReason)
        {
            this.received = received;
            this.skippedAlreadyKnown = skippedAlreadyKnown;
            this.appendedNew = appendedNew;
            this.rejectedInvalid = rejectedInvalid;
            this.firstRejectionReason = firstRejectionReason;
        }
    }

    private readonly int _capacity;
    private readonly List<DjiCameraPoseProvider.TelemetryVelocitySample> _samples = new();

    public TelemetryVelocityHistory(int capacity)
    {
        _capacity = Math.Max(1, capacity);
    }

    public IReadOnlyList<DjiCameraPoseProvider.TelemetryVelocitySample> Samples => _samples;
    public int Count => _samples.Count;
    public long OldestTimestampNs => _samples.Count == 0 ? 0L : _samples[0].timestampNs;
    public long NewestTimestampNs => _samples.Count == 0 ? 0L : _samples[_samples.Count - 1].timestampNs;

    public bool TryAdd(DjiCameraPoseProvider.TelemetryVelocitySample sample, out string reason)
    {
        reason = string.Empty;
        if (sample.timestampNs <= 0L)
        {
            reason = "ZERO_TIMESTAMP";
            return false;
        }

        var velocity = sample.navigationVelocityMetersPerSecond;
        if (!float.IsFinite(velocity.x) || !float.IsFinite(velocity.y) || !float.IsFinite(velocity.z))
        {
            reason = "NONFINITE_VELOCITY";
            return false;
        }

        if (_samples.Count > 0)
        {
            var newestTimestampNs = NewestTimestampNs;
            if (sample.timestampNs == newestTimestampNs)
            {
                reason = "DUPLICATE_TIMESTAMP";
                return false;
            }

            if (sample.timestampNs < newestTimestampNs)
            {
                reason = "NON_MONOTONIC_TIMESTAMP";
                return false;
            }
        }

        _samples.Add(sample);
        if (_samples.Count > _capacity)
            _samples.RemoveAt(0);
        return true;
    }

    /// <summary>
    /// Merges a complete bridge snapshot without treating its previously
    /// consumed chronological prefix as a new out-of-order telemetry source.
    /// Samples newer than the history at sync start still use TryAdd, so an
    /// actually new backward sample remains diagnosable.
    /// </summary>
    public SyncResult SyncBridgeSamples(IEnumerable<DjiCameraPoseProvider.TelemetryVelocitySample> bridgeSamples)
    {
        var received = 0;
        var skippedAlreadyKnown = 0;
        var appendedNew = 0;
        var rejectedInvalid = 0;
        var firstRejectionReason = string.Empty;
        var newestTimestampAtSyncStart = NewestTimestampNs;

        foreach (var sample in bridgeSamples)
        {
            received++;

            // The bridge deliberately returns its bounded history on every
            // poll. This known prefix is a replay, not a source fault.
            if (sample.timestampNs > 0L && sample.timestampNs <= newestTimestampAtSyncStart)
            {
                skippedAlreadyKnown++;
                continue;
            }

            if (TryAdd(sample, out var rejectionReason))
            {
                appendedNew++;
                continue;
            }

            rejectedInvalid++;
            if (string.IsNullOrEmpty(firstRejectionReason))
                firstRejectionReason = rejectionReason;
        }

        return new SyncResult(
            received,
            skippedAlreadyKnown,
            appendedNew,
            rejectedInvalid,
            firstRejectionReason);
    }

    public bool TryInterpolate(
        long targetTimestampNs,
        long maximumBracketGapNs,
        out Vector3 navigationVelocityMetersPerSecond,
        out string reason)
    {
        navigationVelocityMetersPerSecond = Vector3.zero;
        reason = string.Empty;
        if (targetTimestampNs <= 0L)
        {
            reason = "ZERO_TARGET_TIMESTAMP";
            return false;
        }

        if (_samples.Count == 0)
        {
            reason = "HISTORY_EMPTY";
            return false;
        }

        DjiCameraPoseProvider.TelemetryVelocitySample? before = null;
        DjiCameraPoseProvider.TelemetryVelocitySample? after = null;
        foreach (var sample in _samples)
        {
            if (sample.timestampNs <= targetTimestampNs)
                before = sample;
            if (sample.timestampNs >= targetTimestampNs)
            {
                after = sample;
                break;
            }
        }

        if (!before.HasValue || !after.HasValue)
        {
            reason = "BRACKET_UNAVAILABLE";
            return false;
        }

        var spanNs = after.Value.timestampNs - before.Value.timestampNs;
        if (spanNs > maximumBracketGapNs)
        {
            reason = "BRACKET_GAP_TOO_LARGE";
            return false;
        }

        var alpha = spanNs == 0L
            ? 0f
            : Mathf.Clamp01((float)((targetTimestampNs - before.Value.timestampNs) / (double)spanNs));
        navigationVelocityMetersPerSecond = Vector3.Lerp(
            before.Value.navigationVelocityMetersPerSecond,
            after.Value.navigationVelocityMetersPerSecond,
            alpha);
        return true;
    }
}
