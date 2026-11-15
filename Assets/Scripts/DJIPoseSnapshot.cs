using System;

[Serializable]
public sealed class DJIPoseSnapshot
{
    public bool sdkReady;
    public bool hasPose;
    // Legacy wall-clock value retained for diagnostics only. It must not be
    // compared with camera-frame timing.
    public long timestampMs;
    // Android monotonic arrival time of the last DJI KeyManager update.
    // This is not a synchronized SDK measurement timestamp.
    public long telemetryArrivalTimestampNs;
    // These are independent KeyManager callback-arrival times. They must not
    // be used as a substitute timestamp for velocity samples.
    public long aircraftAttitudeArrivalTimestampNs;
    public long gimbalAttitudeArrivalTimestampNs;
    public long telemetryWallClockTimestampMs;
    public string telemetryTimestampSource;
    public AircraftPose aircraft = new();
    public GimbalPose gimbal = new();
    public AircraftBattery aircraftBattery = new();
    public PositionAudit positionAudit = new();

    [Serializable]
    public sealed class AircraftPose
    {
        public double latitude;
        public double longitude;
        public double altitude;
        public double pitch;
        public double roll;
        public double yaw;
        public bool hasLocation;
        public bool hasAltitude;
        public bool hasAttitude;
    }

    [Serializable]
    public sealed class GimbalPose
    {
        public double pitch;
        public double roll;
        public double yaw;
        public double yawRelativeToAircraftHeading;
        public bool hasAttitude;
        public bool hasYawRelativeToAircraftHeading;
    }

    [Serializable]
    public sealed class AircraftBattery
    {
        // hasPercent distinguishes a real callback value of 0 from missing
        // battery telemetry, which the HUD renders as BAT --.
        public int percent;
        public bool hasPercent;
    }

    [Serializable]
    public sealed class PositionAudit
    {
        public bool aircraftLocation2DSupport;
        public string aircraftLocation2DStatus;
        public string aircraftLocation2DRaw;
        public bool aircraftLocation3DSupport;
        public string aircraftLocation3DStatus;
        public string aircraftLocation3DRaw;
        // KeyAircraftLocation3D callback-arrival samples. Altitude remains
        // diagnostic only until its datum has been physically validated.
        public int aircraftPositionHistoryCount;
        public long newestAircraftPositionTimestampNs;
        public AircraftPositionSample[] aircraftPositionHistory = Array.Empty<AircraftPositionSample>();
        public string altitudeReferenceValidation;
        public bool keyAltitudeSupport;
        public string keyAltitudeStatus;
        public string keyAltitudeRaw;
        public ScalarAltitudeSample[] altitudeHistory = Array.Empty<ScalarAltitudeSample>();
        public bool aircraftVelocitySupport;
        public string aircraftVelocityStatus;
        public string aircraftVelocityRaw;
        // Diagnostic-only NED velocity from KeyAircraftVelocity. It is never
        // used as a telemetry-rendered camera position.
        public bool hasAircraftVelocity;
        public float aircraftVelocityX;
        public float aircraftVelocityY;
        public float aircraftVelocityZ;
        // Velocity samples use their own Android-monotonic callback times.
        // Unit validation remains pending; the current project convention is m/s.
        public long latestVelocityTimestampNs;
        public int velocityHistoryCount;
        public long newestVelocityHistoryTimestampNs;
        public VelocitySample[] velocityHistory = Array.Empty<VelocitySample>();
        public string velocityUnitValidation;
        public bool gpsSatelliteCountSupport;
        public string gpsSatelliteCountStatus;
        public string gpsSatelliteCountRaw;
        public bool hasGpsSatelliteCount;
        public bool gpsSignalLevelSupport;
        public string gpsSignalLevelStatus;
        public string gpsSignalLevelRaw;
        public bool gpsIsValidSupport;
        public string gpsIsValidStatus;
        public string gpsIsValidRaw;
        public bool gpsInterferenceStateSupport;
        public string gpsInterferenceStateStatus;
        public string gpsInterferenceStateRaw;
        public string gpsInterferenceStateError;
        public int gpsInterferenceStateHistoryCount;
        public long newestGpsInterferenceStateTimestampNs;
        public HorizontalQualitySample[] gpsInterferenceStateHistory = Array.Empty<HorizontalQualitySample>();
        public bool fusionPositioningLevelSupport;
        public string fusionPositioningLevelStatus;
        public string fusionPositioningLevelRaw;
        public string fusionPositioningLevelError;
        public int fusionPositioningLevelHistoryCount;
        public long newestFusionPositioningLevelTimestampNs;
        public HorizontalQualitySample[] fusionPositioningLevelHistory = Array.Empty<HorizontalQualitySample>();
        public bool homeLocationSupport;
        public string homeLocationStatus;
        public string homeLocationRaw;
        public bool homeLocationSetSupport;
        public string homeLocationSetStatus;
        public string homeLocationSetRaw;
        public bool takeoffLocationAltitudeSupport;
        public string takeoffLocationAltitudeStatus;
        public string takeoffLocationAltitudeRaw;
        public bool heightAboveSeaLevelSupport;
        public string heightAboveSeaLevelStatus;
        public string heightAboveSeaLevelRaw;
        public HeightAboveSeaLevelSample[] heightAboveSeaLevelHistory = Array.Empty<HeightAboveSeaLevelSample>();
        // Contains every HASEL callback, including values rejected from the
        // accepted history because their DJI quality is non-positive.
        public HeightAboveSeaLevelCallbackSample[] heightAboveSeaLevelCallbackHistory = Array.Empty<HeightAboveSeaLevelCallbackSample>();
        public bool relativeToTakeoffPositionSupport;
        public string relativeToTakeoffPositionStatus;
        public string relativeToTakeoffPositionRaw;
        public RelativePositionSample[] relativeToTakeoffHistory = Array.Empty<RelativePositionSample>();
    }

    [Serializable]
    public sealed class VelocitySample
    {
        public long timestampNs;
        public double northMetersPerSecond;
        public double eastMetersPerSecond;
        public double downMetersPerSecond;
    }

    [Serializable]
    public sealed class AircraftPositionSample
    {
        public long timestampNs;
        public double latitude;
        public double longitude;
        public double altitude;
    }

    [Serializable]
    public sealed class ScalarAltitudeSample
    {
        public long timestampNs;
        public double value;
    }

    [Serializable]
    public sealed class HeightAboveSeaLevelSample
    {
        public long timestampNs;
        public double height;
        public int quality;
    }

    [Serializable]
    public sealed class HeightAboveSeaLevelCallbackSample
    {
        public long timestampNs;
        public bool hasHeight;
        public double height;
        public bool hasQuality;
        public int quality;
        public string status;
    }

    [Serializable]
    public sealed class RelativePositionSample
    {
        public long timestampNs;
        public double x;
        public double y;
        public double z;
    }

    [Serializable]
    public sealed class HorizontalQualitySample
    {
        public long timestampNs;
        public string rawValue;
        public string status;
        public string error;
    }
}
