using NUnit.Framework;

public sealed class DjiTelemetryValidationLoggerWorldPathTests
{
    [Test]
    public void ValidRotationPropagationSnapshot_MapsResidualsToVisualLatencyJson()
    {
        var snapshot = new WorldRotationPropagationEquivalenceSnapshot(
            valid: true,
            telemetryTimestampNs: 987654321L,
            sampleCount: 12,
            latestTranslationResidualM: 0.125f,
            latestRotationResidualDeg: 0.25f,
            maxTranslationResidualM: 0.5f,
            maxRotationResidualDeg: 0.75f,
            productionBaselineTimestampNs: 100L,
            resolvedWorldBaselineTimestampNs: 100L,
            historicalBaselineResolved: true,
            worldBaselineHistoryAgeMs: 12.5f,
            worldBaselineHistoryCount: 3,
            baselineResolveFailureReason: string.Empty);

        Assert.That(
            DjiTelemetryValidationLogger.TryBuildWorldRotationPropagationEquivalenceJsonLine(
                "session-456", snapshot, finalSummary: true, out var jsonLine),
            Is.True);

        StringAssert.Contains("\"recordType\":\"WORLD_ROTATION_PROPAGATION_EQUIVALENCE\"", jsonLine);
        StringAssert.Contains("\"worldRotationPropagationRecordKind\":\"FINAL\"", jsonLine);
        StringAssert.Contains("\"worldRotationPropagationSampleCount\":12", jsonLine);
        StringAssert.Contains("\"worldRotationPropagationTelemetryTimestampNs\":987654321", jsonLine);
        StringAssert.Contains("\"worldRotationPropagationMaxTranslationResidualM\":0.5", jsonLine);
        StringAssert.Contains("\"worldRotationPropagationMaxRotationResidualDeg\":0.75", jsonLine);
        StringAssert.Contains("\"productionBaselineTimestampNs\":100", jsonLine);
        StringAssert.Contains("\"resolvedWorldBaselineTimestampNs\":100", jsonLine);
        StringAssert.Contains("\"historicalBaselineResolved\":true", jsonLine);
        StringAssert.Contains("\"worldBaselineHistoryAgeMs\":12.5", jsonLine);
        StringAssert.Contains("\"worldBaselineHistoryCount\":3", jsonLine);
    }

    [Test]
    public void InvalidRotationPropagationSnapshot_DoesNotEmitJson()
    {
        Assert.That(
            DjiTelemetryValidationLogger.TryBuildWorldRotationPropagationEquivalenceJsonLine(
                "session-456", default, finalSummary: false, out var jsonLine),
            Is.False);
        Assert.That(jsonLine, Is.Null);
    }

    [Test]
    public void HorizontalGnssWorldShadow_MapsCandidateAndReacquisitionFieldsToVisualLatencyJson()
    {
        var shadow = HorizontalGnssWorldShadowSnapshot.Valid(
            visualBaselineTimestampNs: 100L,
            gnssBaselineTimestampNs: 90L,
            gnssBaselineAgeMilliseconds: 10d,
            targetTimestampNs: 200L,
            gnssTargetTimestampNs: 180L,
            gnssTargetAgeMilliseconds: 20d,
            navigationDelta: new UnityEngine.Vector3(1f, 2f, 0f),
            worldDelta: new UnityEngine.Vector3(3f, 4f, 5f),
            candidateWorldFromCamera: new UnityEngine.Pose(new UnityEngine.Vector3(6f, 7f, 8f), UnityEngine.Quaternion.identity),
            worldFromNavigationRotation: UnityEngine.Quaternion.identity);
        var reacquisition = new HorizontalGnssWorldShadowReacquisitionSnapshot(
            hasComparison: true,
            visualBaselineTimestampNs: 100L,
            targetTimestampNs: 200L,
            candidateWorldFromCamera: shadow.candidateWorldFromCamera,
            visualWorldFromCamera: shadow.candidateWorldFromCamera,
            worldPositionError: new UnityEngine.Vector3(0.1f, 0.2f, 0.3f),
            navigationHorizontalError: new UnityEngine.Vector2(0.4f, 0.5f),
            navigationHorizontalErrorMagnitudeMeters: 0.6403124f);
        var diagnostics = new HorizontalGnssWorldShadowOutageDiagnostics(
            active: true,
            productionRenderSource: DjiRenderPoseSource.None,
            boardHiddenAgeMilliseconds: 5000d,
            productionRotationPropagationExpired: true,
            timeInNoneMilliseconds: 3000d,
            targetGnssValid: true,
            targetGnssAgeMilliseconds: 20d,
            candidateValidWhileProductionNone: true,
            staleTargetGapCount: 1,
            staleTargetGapDurationMilliseconds: 450d,
            reacquisitionCandidateValid: true,
            reacquisitionCandidateAgeMilliseconds: 20d,
            reacquisitionHorizontalResidualMeters: 0.6403124f);

        Assert.That(
            DjiTelemetryValidationLogger.TryBuildWorldHorizontalGnssShadowJsonLine(
                "session-gnss", shadow, reacquisition, diagnostics, finalSummary: false, out var jsonLine),
            Is.True);

        StringAssert.Contains("\"recordType\":\"WORLD_HORIZONTAL_GNSS_SHADOW\"", jsonLine);
        StringAssert.Contains("\"horizontalGnssWorldShadowValid\":true", jsonLine);
        StringAssert.Contains("\"visualBaselineTimestampNs\":100", jsonLine);
        StringAssert.Contains("\"gnssBaselineTimestampNs\":90", jsonLine);
        StringAssert.Contains("\"northDeltaM\":1", jsonLine);
        StringAssert.Contains("\"eastDeltaM\":2", jsonLine);
        StringAssert.Contains("\"worldDeltaZ\":5", jsonLine);
        StringAssert.Contains("\"cameraLeverArmApplied\":false", jsonLine);
        StringAssert.Contains("\"reacquisitionHorizontalErrorM\":0.6403124", jsonLine);
        StringAssert.Contains("\"diagnosticShadowActive\":true", jsonLine);
        StringAssert.Contains("\"productionRenderSource\":\"None\"", jsonLine);
        StringAssert.Contains("\"candidateValidWhileProductionNone\":true", jsonLine);
        StringAssert.Contains("\"reacquisitionCandidateAgeMs\":20", jsonLine);
    }

    [Test]
    public void InvalidHorizontalGnssWorldShadow_EmitsFailureWithoutCandidateCoordinates()
    {
        var invalid = HorizontalGnssWorldShadowSnapshot.Invalid("GNSS_TARGET_STALE", 100L, 90L, 10d, 500L, 100L, 400d);

        Assert.That(
            DjiTelemetryValidationLogger.TryBuildWorldHorizontalGnssShadowJsonLine(
                "session-gnss", invalid, default, default, finalSummary: true, out var jsonLine),
            Is.True);

        StringAssert.Contains("\"horizontalGnssWorldShadowValid\":false", jsonLine);
        StringAssert.Contains("\"horizontalGnssWorldShadowFailureReason\":\"GNSS_TARGET_STALE\"", jsonLine);
        StringAssert.Contains("\"candidateWorldPositionX\":null", jsonLine);
    }

    [Test]
    public void SmoothingShadow_MapsProductionAndReacquisitionFieldsToVisualLatencyJson()
    {
        var raw = HorizontalGnssWorldShadowSnapshot.Valid(
            100L, 90L, 10d, 200L, 180L, 20d,
            new UnityEngine.Vector3(1f, 2f, 0f),
            new UnityEngine.Vector3(3f, 4f, 0f),
            UnityEngine.Pose.identity,
            UnityEngine.Quaternion.identity);
        var smoothingEstimate = new HorizontalGnssSmoothingEstimate(
            valid: true,
            state: HorizontalGnssSmoothingState.FollowingFreshGnss,
            failureReason: string.Empty,
            targetTimestampNs: 200L,
            rawGnssTargetTimestampNs: 180L,
            rawGnssTarget: new UnityEngine.Vector2(1f, 2f),
            smoothedNavigationDelta: new UnityEngine.Vector2(0.25f, 0.5f),
            desiredCorrection: new UnityEngine.Vector2(0.4f, 0.8f),
            appliedCorrection: new UnityEngine.Vector2(0.1f, 0.2f),
            correctionSpeedMetersPerSecond: 2.5f,
            tauSeconds: 0.5f,
            maximumCorrectionSpeedMetersPerSecond: 2.5f);
        var smoothed = HorizontalGnssSmoothingShadowSnapshot.FromEstimate(
            100L, 200L, raw, smoothingEstimate,
            new UnityEngine.Vector3(0.25f, 0.5f, 0f),
            new UnityEngine.Vector3(0.25f, 0.5f, 0f),
            UnityEngine.Pose.identity,
            UnityEngine.Quaternion.identity,
            hasCandidateRotation: false,
            rawCandidateStepMagnitudeMeters: 0.7f);
        var smoothingReacquisition = new HorizontalGnssSmoothingReacquisitionSnapshot(
            true, 100L, 200L, 0.6f, 0.3f, 1.2f, UnityEngine.Pose.identity, UnityEngine.Pose.identity);
        var verticalEstimate = new VerticalAltitudeSmoothingEstimate(
            VerticalAltitudeLocalizationState.Active,
            string.Empty,
            200L,
            180L,
            -0.2f,
            -0.1f,
            -0.1f,
            0.5f,
            0.4f,
            sourceAccepted: true);
        var vertical = new VerticalAltitudeProductionSnapshot(
            baselineAvailable: true,
            sourceFresh: true,
            baselineAltitude: 100d,
            currentAltitude: 100.2d,
            baselineAltitudeTimestampNs: 100L,
            altitudeSourceTimestampNs: 180L,
            altitudeAgeMilliseconds: 20d,
            rawDeltaAltitudeUp: 0.2f,
            rawDeltaNavigationDown: -0.2f,
            smoothedDeltaNavigationDown: -0.1f,
            worldVerticalContribution: new UnityEngine.Vector3(0f, 0.1f, 0f),
            estimate: verticalEstimate);

        Assert.That(
            DjiTelemetryValidationLogger.TryBuildWorldHorizontalGnssShadowJsonLine(
                "session-smoothing", raw, default, default, smoothed, smoothingReacquisition,
                productionHorizontalGnssApplied: true,
                productionHorizontalGnssState: HorizontalGnssSmoothingState.FollowingFreshGnss,
                productionHorizontalGnssReason: string.Empty,
                qualityAdmission: new HorizontalGnssQualityAdmissionSnapshot(
                    hasObservedGnssCallback: true,
                    satelliteCount: 25,
                    trusted: true,
                    transition: HorizontalGnssQualityTransition.EnteredTrusted,
                    confirmationCount: 3,
                    admissionReason: "SATELLITE_ENTER_CONFIRMED",
                    sourceTimestampNs: 200L),
                productionVerticalAltitude: vertical,
                finalSummary: false,
                out var jsonLine),
            Is.True);

        StringAssert.Contains("\"smoothedGnssCandidateValid\":true", jsonLine);
        StringAssert.Contains("\"smootherState\":\"FollowingFreshGnss\"", jsonLine);
        StringAssert.Contains("\"smoothedNorthM\":0.25", jsonLine);
        StringAssert.Contains("\"smoothingTauSeconds\":0.5", jsonLine);
        StringAssert.Contains("\"maxCorrectionSpeedMps\":2.5", jsonLine);
        StringAssert.Contains("\"targetAccepted\":false", jsonLine);
        StringAssert.Contains("\"acceptedTargetNorthM\":0", jsonLine);
        StringAssert.Contains("\"temporalConsistencyCount\":0", jsonLine);
        StringAssert.Contains("\"smoothedReacquisitionHorizontalErrorM\":0.3", jsonLine);
        StringAssert.Contains("\"smoothedRawReacquisitionHorizontalErrorM\":0.6", jsonLine);
        StringAssert.Contains("\"smoothedHeldPositionReacquisitionHorizontalErrorM\":1.2", jsonLine);
        StringAssert.Contains("\"rawStepMagnitudeM\":0.7", jsonLine);
        StringAssert.DoesNotContain("\"fusedCandidateValid\"", jsonLine);
        StringAssert.DoesNotContain("\"velocityCoverageMs\"", jsonLine);
        StringAssert.Contains("\"productionHorizontalGnssApplied\":true", jsonLine);
        StringAssert.Contains("\"productionHorizontalGnssState\":\"FollowingFreshGnss\"", jsonLine);
        StringAssert.Contains("\"gnssSatelliteCount\":25", jsonLine);
        StringAssert.Contains("\"horizontalGnssQualityTrusted\":true", jsonLine);
        StringAssert.Contains("\"horizontalGnssQualityTransition\":\"EnteredTrusted\"", jsonLine);
        StringAssert.Contains("\"qualityConfirmationCount\":3", jsonLine);
        StringAssert.Contains("\"qualityAdmissionReason\":\"SATELLITE_ENTER_CONFIRMED\"", jsonLine);
        StringAssert.Contains("\"verticalProductionSource\":\"Location3DAltitude\"", jsonLine);
        StringAssert.Contains("\"verticalLocalizationState\":\"Active\"", jsonLine);
        StringAssert.Contains("\"rawDeltaAltitudeUp\":0.2", jsonLine);
        StringAssert.Contains("\"smoothedDeltaNavigationDown\":-0.1", jsonLine);
    }
}
