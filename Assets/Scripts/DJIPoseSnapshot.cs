using System;

[Serializable]
public sealed class DJIPoseSnapshot
{
    public bool sdkReady;
    public bool hasPose;
    public long timestampMs;
    public AircraftPose aircraft = new();
    public GimbalPose gimbal = new();

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
}
