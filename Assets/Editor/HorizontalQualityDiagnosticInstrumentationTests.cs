using NUnit.Framework;

public sealed class HorizontalQualityDiagnosticInstrumentationTests
{
    [Test]
    public void CallbackJson_PreservesInterferenceTimestampAndRawEnum()
    {
        var json = DjiTelemetryValidationLogger.BuildHorizontalQualitySampleJsonLine(
            "session",
            900L,
            new HorizontalQualityCallbackRecord(
                HorizontalQualitySampleKind.GpsInterferenceState,
                123L,
                "WARNING",
                "KEY_AVAILABLE_VALID",
                null));

        Assert.That(json, Does.Contain("\"recordType\":\"HorizontalQualitySample\""));
        Assert.That(json, Does.Contain("\"sourceTimestampNs\":123"));
        Assert.That(json, Does.Contain("\"rawValue\":\"WARNING\""));
        Assert.That(json, Does.Contain("\"status\":\"KEY_AVAILABLE_VALID\""));
    }

    [Test]
    public void CallbackJson_PreservesFusionTimestampAndRawEnum()
    {
        var json = DjiTelemetryValidationLogger.BuildHorizontalQualitySampleJsonLine(
            "session",
            900L,
            new HorizontalQualityCallbackRecord(
                HorizontalQualitySampleKind.FusionPositioningLevel,
                456L,
                "STRONG",
                "KEY_AVAILABLE_VALID",
                null));

        Assert.That(json, Does.Contain("\"source\":\"FusionPositioningLevel\""));
        Assert.That(json, Does.Contain("\"sourceTimestampNs\":456"));
        Assert.That(json, Does.Contain("\"rawValue\":\"STRONG\""));
    }

    [Test]
    public void CallbackJson_PreservesUnsupportedAndCallbackErrorStates()
    {
        var unsupported = DjiTelemetryValidationLogger.BuildHorizontalQualitySampleJsonLine(
            "session",
            900L,
            new HorizontalQualityCallbackRecord(
                HorizontalQualitySampleKind.GpsInterferenceState,
                123L,
                "null",
                "KEY_UNSUPPORTED",
                null));
        var callbackError = DjiTelemetryValidationLogger.BuildHorizontalQualitySampleJsonLine(
            "session",
            900L,
            new HorizontalQualityCallbackRecord(
                HorizontalQualitySampleKind.FusionPositioningLevel,
                456L,
                "null",
                "CALLBACK_ERROR",
                "not supported"));

        Assert.That(unsupported, Does.Contain("\"status\":\"KEY_UNSUPPORTED\""));
        Assert.That(callbackError, Does.Contain("\"status\":\"CALLBACK_ERROR\""));
        Assert.That(callbackError, Does.Contain("\"error\":\"not supported\""));
    }

    [Test]
    public void Deduplicator_SkipsReplayedSamplesButAcceptsTwoActualCallbacks()
    {
        var deduplicator = new HorizontalQualitySampleDeduplicator();

        Assert.That(deduplicator.TryAccept(HorizontalQualitySampleKind.GpsInterferenceState, 100L), Is.True);
        Assert.That(deduplicator.TryAccept(HorizontalQualitySampleKind.GpsInterferenceState, 100L), Is.False);
        Assert.That(deduplicator.TryAccept(HorizontalQualitySampleKind.GpsInterferenceState, 200L), Is.True);
        Assert.That(deduplicator.TryAccept(HorizontalQualitySampleKind.FusionPositioningLevel, 100L), Is.True);
        Assert.That(deduplicator.TryAccept(HorizontalQualitySampleKind.GpsInterferenceState, 99L), Is.False);
    }

    [Test]
    public void PeriodicSnapshot_ExposesLatestQualityValuesAndAges()
    {
        var audit = new DJIPoseSnapshot.PositionAudit
        {
            gpsSatelliteCountRaw = "25",
            gpsSignalLevelRaw = "LEVEL_5",
            gpsIsValidRaw = "true",
            gpsInterferenceStateSupport = true,
            gpsInterferenceStateStatus = "KEY_AVAILABLE_VALID",
            gpsInterferenceStateRaw = "OK",
            gpsInterferenceStateHistory = new[]
            {
                new DJIPoseSnapshot.HorizontalQualitySample { timestampNs = 700L, rawValue = "OK", status = "KEY_AVAILABLE_VALID" }
            },
            fusionPositioningLevelSupport = true,
            fusionPositioningLevelStatus = "KEY_AVAILABLE_VALID",
            fusionPositioningLevelRaw = "STRONG",
            fusionPositioningLevelHistory = new[]
            {
                new DJIPoseSnapshot.HorizontalQualitySample { timestampNs = 100_000_700L, rawValue = "STRONG", status = "KEY_AVAILABLE_VALID" }
            }
        };

        var json = DjiTelemetryValidationLogger.BuildHorizontalQualitySnapshotJsonLine(1_000_000_700L, 25, audit);

        Assert.That(json, Does.Contain("\"gpsInterferenceState\":\"OK\""));
        Assert.That(json, Does.Contain("\"gpsInterferenceAgeMs\":1000"));
        Assert.That(json, Does.Contain("\"fusionPositioningLevel\":\"STRONG\""));
        Assert.That(json, Does.Contain("\"fusionPositioningLevelAgeMs\":900"));
        Assert.That(json, Does.Contain("\"satelliteCount\":25"));
    }

    [Test]
    public void QualityDiagnostics_DoNotMutateProductionHorizontalGate()
    {
        var gate = new HorizontalGnssQualityAdmissionGate();
        gate.Update(100L, 23);
        gate.Update(200L, 23);
        gate.Update(300L, 23);
        var before = gate.Snapshot;

        DjiTelemetryValidationLogger.BuildHorizontalQualitySampleJsonLine(
            "session",
            900L,
            new HorizontalQualityCallbackRecord(
                HorizontalQualitySampleKind.GpsInterferenceState,
                400L,
                "CRITICAL",
                "KEY_AVAILABLE_VALID",
                null));

        Assert.That(before.trusted, Is.True);
        Assert.That(gate.Snapshot.trusted, Is.True);
        Assert.That(HorizontalGnssQualityAdmissionGate.ProvisionalEnterSatelliteCount, Is.EqualTo(23));
        Assert.That(HorizontalGnssQualityAdmissionGate.ProvisionalExitSatelliteCount, Is.EqualTo(21));
    }
}
