using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public sealed class RotationPropagationTransformTests
{
    private const float DefaultHighConfidenceRmsPixels = 2f;
    private const float DefaultHighConfidenceCornerResidualPixels = 4f;
    private const float FourMarkerMaximumNormalizedRms = 0.065f;
    private const float FourMarkerMaximumNormalizedCornerResidual = 0.10f;
    private const float FourMarkerHardMaximumRmsPixels = 25f;
    private const float FourMarkerHardMaximumCornerResidualPixels = 35f;

    [Test]
    public void OutageGroundTruth_VisualToPropagatedCreatesOneOutageAndNoneKeepsItOpen()
    {
        var tracker = new DjiOutageVisualGroundTruthTracker();
        var pre = TrustedVisualPose(new Vector3(0f, 0f, 2f), timestampNs: 100L);

        Assert.That(tracker.TryBegin(0, pre, 110L, 3, HorizontalTranslationRenderMode.GuardedGnssRendered, out var outage), Is.True);
        Assert.That(outage.outageId, Is.Zero);
        // TELEMETRY_PROPAGATED -> NONE intentionally does not begin another event.
        Assert.That(tracker.TryBegin(0, pre, 120L, 3, HorizontalTranslationRenderMode.GuardedGnssRendered, out _), Is.False);
        Assert.That(tracker.HasActiveOutage, Is.True);
    }

    [Test]
    public void OutageGroundTruth_FreshRelocalizationClosesSameOutage()
    {
        var tracker = new DjiOutageVisualGroundTruthTracker();
        var pre = TrustedVisualPose(Vector3.zero, timestampNs: 100L);
        Assert.That(tracker.TryBegin(0, pre, 110L, 1, HorizontalTranslationRenderMode.ShadowOnly, out var started), Is.True);

        Assert.That(tracker.TryComplete(out var completed), Is.True);
        Assert.That(completed.outageId, Is.EqualTo(started.outageId));
        Assert.That(completed.horizontalRenderMode, Is.EqualTo(HorizontalTranslationRenderMode.ShadowOnly));
        Assert.That(tracker.HasActiveOutage, Is.False);
    }

    [Test]
    public void OutageGroundTruth_ModeToggleMarksEventAsExcludedFromCleanAB()
    {
        var tracker = new DjiOutageVisualGroundTruthTracker();
        Assert.That(
            tracker.TryBegin(2, TrustedVisualPose(Vector3.zero, 100L), 110L, 4,
                HorizontalTranslationRenderMode.GuardedGnssRendered, out _),
            Is.True);

        tracker.MarkModeChangedDuringOutage();
        Assert.That(tracker.TryComplete(out var completed), Is.True);
        Assert.That(completed.modeChangedDuringOutage, Is.True);
        Assert.That(completed.horizontalModeSegmentId, Is.EqualTo(4));
    }

    [Test]
    public void OutageGroundTruth_RepeatedCyclesKeepSequentialIds()
    {
        var tracker = new DjiOutageVisualGroundTruthTracker();
        for (var index = 0; index < 10; index++)
        {
            Assert.That(
                tracker.TryBegin(0, TrustedVisualPose(Vector3.zero, index + 1L), index + 2L, 0,
                    HorizontalTranslationRenderMode.GuardedGnssRendered, out var outage),
                Is.True);
            Assert.That(outage.outageId, Is.EqualTo(index));
            Assert.That(tracker.TryComplete(out _), Is.True);
        }
    }

    [Test]
    public void OutageGroundTruth_VisualDisplacementUsesBoardAxesWithoutTelemetry()
    {
        var pre = TrustedVisualPose(new Vector3(2f, 3f, 4f), 100L);
        var post = TrustedVisualPose(new Vector3(1.5f, 2.5f, 3f), 200L);

        var displacement = DjiOutageVisualGroundTruthTracker.CameraDisplacementInBoard(pre, post);

        Assert.That(displacement.x, Is.EqualTo(0.5f).Within(0.0001f));
        Assert.That(displacement.y, Is.EqualTo(0.5f).Within(0.0001f));
        Assert.That(displacement.z, Is.EqualTo(1f).Within(0.0001f));
        Assert.That(new Vector2(displacement.x, displacement.y).magnitude, Is.EqualTo(Mathf.Sqrt(0.5f)).Within(0.0001f));
        Assert.That(Mathf.Abs(displacement.z), Is.EqualTo(1f).Within(0.0001f));
    }

    [Test]
    public void OutageGroundTruth_PredictionHorizontalErrorUsesBoardPlaneWithoutTelemetry()
    {
        var predicted = new Pose(new Vector3(0f, 0f, 2f), Quaternion.identity);
        var visual = new Pose(new Vector3(0.5f, 0f, 2f), Quaternion.identity);

        Assert.That(
            DjiOutageVisualGroundTruthTracker.HorizontalPredictionErrorInBoard(predicted, visual),
            Is.EqualTo(0.5f).Within(0.0001f));
    }

    [Test]
    public void OutageGroundTruth_HandoverSummaryRetainsCapturedPredictionsAfterLiveStateWouldReset()
    {
        var tracker = new DjiOutageVisualGroundTruthTracker();
        var pre = TrustedVisualPose(Vector3.zero, timestampNs: 100L);
        Assert.That(
            tracker.TryBegin(0, pre, 110L, 1, HorizontalTranslationRenderMode.GuardedGnssRendered, out var outage),
            Is.True);

        // These represent the immutable pre-VISUAL handover snapshot. The
        // provider may reset live propagation immediately afterwards.
        var rotationOnly = new DjiOutagePredictionPose(
            true,
            new Pose(new Vector3(0.10f, 0.20f, 2f), Quaternion.identity));
        var guarded = new DjiOutagePredictionPose(
            true,
            new Pose(new Vector3(0.35f, 0.20f, 2f), Quaternion.identity));
        Assert.That(tracker.TryComplete(out _), Is.True);

        var post = TrustedVisualPose(new Vector3(0.40f, 0.20f, 2f), timestampNs: 200L);
        var summary = new DjiOutageReacquisitionSummary(
            outage,
            DjiOutageReacquisitionMode.PropagatedReacquisition,
            post,
            outageDurationMilliseconds: 90d,
            propagationDurationMilliseconds: 90d,
            visualDisplacementValid: true,
            visualDeltaInBoard: Vector3.zero,
            visualRotationDifferenceDegrees: 0f,
            rotationOnly,
            guarded,
            default,
            default,
            GuardedHorizontalTranslationState.Active,
            appliedCorrectionMeters: 0.25f,
            candidateCorrectionMeters: 0.30f,
            blendAlpha: 1f);

        Assert.That(summary.rotationOnly.valid, Is.True);
        Assert.That(summary.guardedRendered.valid, Is.True);
        Assert.That(summary.guardedStateAtHandover, Is.EqualTo(GuardedHorizontalTranslationState.Active));
        Assert.That(summary.appliedCorrectionMeters, Is.EqualTo(0.25f));
        Assert.That(summary.guardedRendered.cameraFromBoard.position, Is.EqualTo(guarded.cameraFromBoard.position));
    }

    [Test]
    public void OutageGroundTruth_BoardDisplacementRemainsExactAcrossIndependentCameraRotations()
    {
        var preBoardFromCamera = new Pose(
            new Vector3(0.10f, -0.20f, 0.30f),
            Quaternion.Euler(7f, 23f, -11f));
        var postBoardFromCamera = new Pose(
            new Vector3(0.45f, -0.10f, 0.55f),
            Quaternion.Euler(-13f, 41f, 19f));
        var pre = new DjiOutageVisualGroundTruthTracker.TrustedVisualPose(
            ReferenceFrameTransforms.Invert(preBoardFromCamera), 100L);
        var post = new DjiOutageVisualGroundTruthTracker.TrustedVisualPose(
            ReferenceFrameTransforms.Invert(postBoardFromCamera), 200L);

        var displacement = DjiOutageVisualGroundTruthTracker.CameraDisplacementInBoard(pre, post);

        Assert.That(displacement.x, Is.EqualTo(0.35f).Within(0.0001f));
        Assert.That(displacement.y, Is.EqualTo(0.10f).Within(0.0001f));
        Assert.That(displacement.z, Is.EqualTo(0.25f).Within(0.0001f));
    }

    [Test]
    public void VisualQuality_FourStableMarkersWithMeasuredCornerResidualPasses()
    {
        Assert.That(
            EvaluateVisualQuality(markerCount: 4, rmsPixels: 3.2f, maxCornerResidualPixels: 5.5f, observedMarkerSizePixels: 100f),
            Is.Empty);
    }

    [Test]
    public void VisualQuality_FourMarkersAboveNormalizedRmsIsDegraded()
    {
        Assert.That(
            EvaluateVisualQuality(markerCount: 4, rmsPixels: 5.5f, maxCornerResidualPixels: 5f, observedMarkerSizePixels: 80f),
            Is.EqualTo("NORMALIZED_RMS"));
    }

    [Test]
    public void VisualQuality_FourMarkersAboveNormalizedCornerResidualIsDegraded()
    {
        Assert.That(
            EvaluateVisualQuality(markerCount: 4, rmsPixels: 3.2f, maxCornerResidualPixels: 10.5f, observedMarkerSizePixels: 100f),
            Is.EqualTo("NORMALIZED_CORNER_RESIDUAL"));
    }

    [Test]
    public void VisualQuality_SameRmsAtLargerMarkerScaleIsBetter()
    {
        var largeMarkerNormalizedRms = DjiVisualPoseQualityPolicy.NormalizePixelResidual(4f, 400f);
        var smallMarkerNormalizedRms = DjiVisualPoseQualityPolicy.NormalizePixelResidual(4f, 50f);

        Assert.That(largeMarkerNormalizedRms, Is.EqualTo(0.01f).Within(0.0001f));
        Assert.That(smallMarkerNormalizedRms, Is.EqualTo(0.08f).Within(0.0001f));
        Assert.That(largeMarkerNormalizedRms, Is.LessThan(smallMarkerNormalizedRms));
        Assert.That(
            EvaluateVisualQuality(markerCount: 4, rmsPixels: 4f, maxCornerResidualPixels: 5f, observedMarkerSizePixels: 400f),
            Is.Empty);
        Assert.That(
            EvaluateVisualQuality(markerCount: 4, rmsPixels: 4f, maxCornerResidualPixels: 5f, observedMarkerSizePixels: 50f),
            Is.EqualTo("NORMALIZED_RMS"));
    }

    [Test]
    public void VisualQuality_CloseRangeFourMarkerPoseIsRescuedByNormalizedScale()
    {
        // Latest physical run: a close board produced roughly 6-7 px RMS and
        // 10-11 px worst residual while markers were approximately 145 px wide.
        Assert.That(
            EvaluateVisualQuality(markerCount: 4, rmsPixels: 6.8f, maxCornerResidualPixels: 10.8f, observedMarkerSizePixels: 145f),
            Is.Empty);
    }

    [Test]
    public void VisualQuality_PathologicalFourMarkerResidualsStillHitHardCaps()
    {
        Assert.That(
            EvaluateVisualQuality(markerCount: 4, rmsPixels: 30f, maxCornerResidualPixels: 5f, observedMarkerSizePixels: 1000f),
            Is.EqualTo("ABSOLUTE_RMS_HARD_CAP"));
        Assert.That(
            EvaluateVisualQuality(markerCount: 4, rmsPixels: 5f, maxCornerResidualPixels: 45f, observedMarkerSizePixels: 1000f),
            Is.EqualTo("ABSOLUTE_CORNER_HARD_CAP"));
    }

    [Test]
    public void VisualQuality_TwoMarkersKeepFourPixelCornerResidualLimit()
    {
        Assert.That(EvaluateVisualQuality(markerCount: 2, rmsPixels: 1.5f, maxCornerResidualPixels: 5.5f), Is.EqualTo("CORNER_RESIDUAL"));
    }

    [Test]
    public void VisualQuality_TwoMarkersKeepTwoPixelRmsLimit()
    {
        Assert.That(EvaluateVisualQuality(markerCount: 2, rmsPixels: 2.1f, maxCornerResidualPixels: 1f), Is.EqualTo("REPROJECTION_RMS"));
    }

    [Test]
    public void VisualQuality_FourMarkersNearImageBorderIsDegraded()
    {
        Assert.That(
            EvaluateVisualQuality(markerCount: 4, rmsPixels: 4f, borderDistancePixels: 20f),
            Is.EqualTo("BORDER_PROXIMITY"));
    }

    [Test]
    public void VisualQuality_FourMarkersWithLargePoseJumpIsDegraded()
    {
        Assert.That(
            EvaluateVisualQuality(markerCount: 4, rmsPixels: 4f, translationJumpMeters: 0.04f),
            Is.EqualTo("POSE_TRANSLATION_JUMP"));
    }

    [Test]
    public void VisualQualityHysteresis_OneMarginalCornerFailureHoldsVisualAuthority()
    {
        const int confirmationFrames = 3;
        var failures = DjiVisualPoseQualityPolicy.AdvanceBorderlineFailureCount(
            0,
            FourMarkerVisualQualityFailureSeverity.Borderline);

        Assert.That(
            DjiVisualPoseQualityPolicy.IsBorderlineFourMarkerNormalizedCornerResidual(
                normalizedCornerResidual: 0.102f,
                maximumNormalizedCornerResidual: FourMarkerMaximumNormalizedCornerResidual,
                borderlineMaximumNormalizedCornerResidual: 0.105f),
            Is.True);
        Assert.That(
            DjiVisualPoseQualityPolicy.ShouldHoldVisualAuthorityForBorderlineFailure(
                hasVisualAuthority: true,
                consecutiveFailures: failures,
                confirmationFrames: confirmationFrames),
            Is.True);
    }

    [Test]
    public void VisualQualityHysteresis_TwoMarginalFailuresThenGoodKeepsVisualAuthority()
    {
        const int confirmationFrames = 3;
        var failures = 0;
        failures = DjiVisualPoseQualityPolicy.AdvanceBorderlineFailureCount(
            failures,
            FourMarkerVisualQualityFailureSeverity.Borderline);
        failures = DjiVisualPoseQualityPolicy.AdvanceBorderlineFailureCount(
            failures,
            FourMarkerVisualQualityFailureSeverity.Borderline);

        Assert.That(
            DjiVisualPoseQualityPolicy.ShouldHoldVisualAuthorityForBorderlineFailure(true, failures, confirmationFrames),
            Is.True);

        failures = DjiVisualPoseQualityPolicy.AdvanceBorderlineFailureCount(
            failures,
            FourMarkerVisualQualityFailureSeverity.Pass);
        Assert.That(failures, Is.Zero);
    }

    [Test]
    public void VisualQualityHysteresis_RepeatedMarginalFailuresDegradeVisualAuthority()
    {
        const int confirmationFrames = 3;
        var failures = 0;
        for (var i = 0; i < confirmationFrames; i++)
        {
            failures = DjiVisualPoseQualityPolicy.AdvanceBorderlineFailureCount(
                failures,
                FourMarkerVisualQualityFailureSeverity.Borderline);
        }

        Assert.That(
            DjiVisualPoseQualityPolicy.ShouldHoldVisualAuthorityForBorderlineFailure(true, failures, confirmationFrames),
            Is.False);
    }

    [Test]
    public void VisualQualityHysteresis_SevereCornerAndBorderFailuresDoNotHoldVisualAuthority()
    {
        Assert.That(
            DjiVisualPoseQualityPolicy.IsBorderlineFourMarkerNormalizedCornerResidual(
                normalizedCornerResidual: 0.106f,
                maximumNormalizedCornerResidual: FourMarkerMaximumNormalizedCornerResidual,
                borderlineMaximumNormalizedCornerResidual: 0.105f),
            Is.False);
        Assert.That(
            DjiVisualPoseQualityPolicy.IsBorderlineFourMarkerBorderProximity(
                borderDistancePixels: 47f,
                minimumRequiredBorderDistancePixels: 64f,
                severeBorderDistancePixels: 48f),
            Is.False);
    }

    [Test]
    public void MotionAwareTranslationJump_FourMarkerCoherentMotionStaysVisual()
    {
        var result = DjiVisualPoseQualityPolicy.EvaluateMotionAwareTranslationJump(
            translationJumpMeters: 0.04f,
            elapsedSeconds: 0.1f,
            hasRotationPrediction: true,
            rotationCompensatedResidualMeters: 0.008f,
            hasTelemetrySpeed: true,
            telemetrySpeedMetersPerSecond: 0.1f,
            maximumNormalJumpMeters: 0.035f,
            immediateRejectJumpMeters: 0.15f,
            motionResidualAllowanceMeters: 0.015f,
            telemetrySpeedAllowanceMultiplier: 1.5f);

        Assert.That(result.decision, Is.EqualTo(VisualTranslationJumpDecision.Accept));
    }

    [Test]
    public void MotionAwareTranslationJump_RepeatedInconsistentBorderlineJumpsConfirmDegradation()
    {
        var result = DjiVisualPoseQualityPolicy.EvaluateMotionAwareTranslationJump(
            translationJumpMeters: 0.045f,
            elapsedSeconds: 0.1f,
            hasRotationPrediction: true,
            rotationCompensatedResidualMeters: 0.07f,
            hasTelemetrySpeed: true,
            telemetrySpeedMetersPerSecond: 0.1f,
            maximumNormalJumpMeters: 0.035f,
            immediateRejectJumpMeters: 0.15f,
            motionResidualAllowanceMeters: 0.015f,
            telemetrySpeedAllowanceMultiplier: 1.5f);

        Assert.That(result.decision, Is.EqualTo(VisualTranslationJumpDecision.Suspect));
        Assert.That(DjiVisualPoseQualityPolicy.IsTranslationJumpConfirmed(3, 3), Is.True);
    }

    [Test]
    public void MotionAwareTranslationJump_LargeImpossibleJumpIsRejectedImmediately()
    {
        var result = DjiVisualPoseQualityPolicy.EvaluateMotionAwareTranslationJump(
            translationJumpMeters: 0.2f,
            elapsedSeconds: 0.1f,
            hasRotationPrediction: true,
            rotationCompensatedResidualMeters: 0.01f,
            hasTelemetrySpeed: true,
            telemetrySpeedMetersPerSecond: 1f,
            maximumNormalJumpMeters: 0.035f,
            immediateRejectJumpMeters: 0.15f,
            motionResidualAllowanceMeters: 0.015f,
            telemetrySpeedAllowanceMultiplier: 1.5f);

        Assert.That(result.decision, Is.EqualTo(VisualTranslationJumpDecision.Reject));
    }

    [Test]
    public void RenderPolicy_UsesVisualPoseWhileFresh()
    {
        var source = RotationPropagationRenderPolicy.Resolve(
            hasVisualPose: true,
            visualPoseAgeSeconds: 0.1f,
            visualPoseFreshnessSeconds: 2f,
            rotationPropagationEnabled: true,
            hasPredictedPose: true,
            propagationDurationSeconds: 0.1f,
            maximumPropagationDurationSeconds: 2f);

        Assert.That(source, Is.EqualTo(DjiRenderPoseSource.Visual));
    }

    [Test]
    public void RenderPolicy_UsesPropagationOnlyAfterVisualPoseIsStale()
    {
        var source = RotationPropagationRenderPolicy.Resolve(
            hasVisualPose: true,
            visualPoseAgeSeconds: 2.1f,
            visualPoseFreshnessSeconds: 2f,
            rotationPropagationEnabled: true,
            hasPredictedPose: true,
            propagationDurationSeconds: 1f,
            maximumPropagationDurationSeconds: 2f);

        Assert.That(source, Is.EqualTo(DjiRenderPoseSource.TelemetryPropagated));
    }

    [Test]
    public void RenderPolicy_UsesPropagationWhenRawPoseIsNotRenderQualified()
    {
        // The provider passes false here for a PnP-accepted but degraded raw
        // pose, keeping PnP acceptance separate from render authority.
        var source = RotationPropagationRenderPolicy.Resolve(
            hasVisualPose: false,
            visualPoseAgeSeconds: 0f,
            visualPoseFreshnessSeconds: 2f,
            rotationPropagationEnabled: true,
            hasPredictedPose: true,
            propagationDurationSeconds: 0.1f,
            maximumPropagationDurationSeconds: 2f);

        Assert.That(source, Is.EqualTo(DjiRenderPoseSource.TelemetryPropagated));
    }

    [Test]
    public void RenderPolicy_ReturnsToVisualWhenBoardReacquires()
    {
        var source = RotationPropagationRenderPolicy.Resolve(
            hasVisualPose: true,
            visualPoseAgeSeconds: 0.05f,
            visualPoseFreshnessSeconds: 2f,
            rotationPropagationEnabled: true,
            hasPredictedPose: true,
            propagationDurationSeconds: 1.5f,
            maximumPropagationDurationSeconds: 2f);

        Assert.That(source, Is.EqualTo(DjiRenderPoseSource.Visual));
    }

    [Test]
    public void ReacquisitionHandover_OneHighConfidenceFrameStaysPropagatedDuringDwell()
    {
        Assert.That(ReacquisitionReady(
            hasHighConfidenceVisualPose: true,
            elapsedMilliseconds: 100,
            positionDifferenceMeters: 0.1f,
            rotationDifferenceDegrees: 1f), Is.False);
    }

    [Test]
    public void ReacquisitionHandover_QualityFailureBeforeDwellCannotTakeVisualAuthority()
    {
        Assert.That(ReacquisitionReady(
            hasHighConfidenceVisualPose: false,
            elapsedMilliseconds: 150,
            positionDifferenceMeters: 0.1f,
            rotationDifferenceDegrees: 1f), Is.False);
    }

    [Test]
    public void ReacquisitionHandover_StableCloseVisualPoseReturnsAuthorityAfterDwell()
    {
        Assert.That(ReacquisitionReady(
            hasHighConfidenceVisualPose: true,
            elapsedMilliseconds: 300,
            positionDifferenceMeters: 0.5f,
            rotationDifferenceDegrees: 4f), Is.True);
    }

    [Test]
    public void ReacquisitionHandover_LargePoseDisagreementStaysPropagated()
    {
        Assert.That(ReacquisitionReady(
            hasHighConfidenceVisualPose: true,
            elapsedMilliseconds: 300,
            positionDifferenceMeters: 1.1f,
            rotationDifferenceDegrees: 4f), Is.False);
        Assert.That(ReacquisitionReady(
            hasHighConfidenceVisualPose: true,
            elapsedMilliseconds: 300,
            positionDifferenceMeters: 0.5f,
            rotationDifferenceDegrees: 6.1f), Is.False);
    }

    [Test]
    public void LongLivedTelemetryReacquisition_UsesQualityAndDwellWithoutPoseDistance()
    {
        const long startTimestampNs = 1_000_000_000L;

        Assert.That(RotationPropagationRenderPolicy.IsLongLivedTelemetryReacquisitionReady(
            hasHighConfidenceVisualPose: true,
            reacquisitionStartTimestampNs: startTimestampNs,
            candidateTimestampNs: startTimestampNs + 300L * 1_000_000L,
            requiredDwellSeconds: 0.25f), Is.True,
            "A >1 m telemetry drift must not reject a stable high-confidence visual relocalization.");
        Assert.That(RotationPropagationRenderPolicy.IsLongLivedTelemetryReacquisitionReady(
            hasHighConfidenceVisualPose: false,
            reacquisitionStartTimestampNs: startTimestampNs,
            candidateTimestampNs: startTimestampNs + 300L * 1_000_000L,
            requiredDwellSeconds: 0.25f), Is.False);
        Assert.That(RotationPropagationRenderPolicy.IsLongLivedTelemetryReacquisitionReady(
            hasHighConfidenceVisualPose: true,
            reacquisitionStartTimestampNs: startTimestampNs,
            candidateTimestampNs: startTimestampNs + 100L * 1_000_000L,
            requiredDwellSeconds: 0.25f), Is.False);
    }

    [Test]
    public void ProvisionalVisualCandidate_PreservesPropagationLifetimeUntilVisualHandover()
    {
        const long propagationStartTimestampNs = 1_000_000_000L;
        const long nowTimestampNs = propagationStartTimestampNs + 2_100L * 1_000_000L;

        Assert.That(RotationPropagationRenderPolicy.ShouldPreservePropagationForProvisionalVisual(
            DjiRenderPoseSource.TelemetryPropagated), Is.True);
        Assert.That(RotationPropagationTiming.AgeSeconds(nowTimestampNs, propagationStartTimestampNs),
            Is.EqualTo(2.1f).Within(0.0001f));
        Assert.That(RotationPropagationRenderPolicy.Resolve(
            hasVisualPose: false,
            visualPoseAgeSeconds: 3f,
            visualPoseFreshnessSeconds: 2f,
            rotationPropagationEnabled: true,
            hasPredictedPose: true,
            propagationDurationSeconds: RotationPropagationTiming.AgeSeconds(nowTimestampNs, propagationStartTimestampNs),
            maximumPropagationDurationSeconds: 2f,
            hasLongLivedTelemetryLocalization: true), Is.EqualTo(DjiRenderPoseSource.TelemetryPropagated));
        Assert.That(RotationPropagationRenderPolicy.ShouldPreservePropagationForProvisionalVisual(
            DjiRenderPoseSource.Visual), Is.False);
    }

    [Test]
    public void ProvisionalVisualTelemetryComparison_DoesNotResetProviderPropagationTimer()
    {
        var gameObject = new GameObject("ProvisionalVisualPropagationTimerTest");
        try
        {
            var provider = gameObject.AddComponent<DjiBoardVisionProvider>();
            SetPrivateField(provider, "rotationPropagationMode", DjiBoardVisionProvider.RotationPropagationMode.Enabled);
            SetPrivateProperty(provider, "CurrentRenderPoseSource", DjiRenderPoseSource.TelemetryPropagated);
            SetPrivateField(provider, "_propagationStartTimestampNs", 1_000_000_000L);

            var providerType = typeof(DjiBoardVisionProvider);
            var pendingType = providerType.GetNestedType("PendingRotationDiagnosticSample", BindingFlags.NonPublic);
            var telemetryType = providerType.GetNestedType("InterpolatedTelemetryRotation", BindingFlags.NonPublic);
            var method = providerType.GetMethod(
                "CaptureShadowBaselineOrCompareReacquisition",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(pendingType, Is.Not.Null);
            Assert.That(telemetryType, Is.Not.Null);
            Assert.That(method, Is.Not.Null);

            method.Invoke(provider, new[]
            {
                Activator.CreateInstance(pendingType),
                Activator.CreateInstance(telemetryType)
            });

            Assert.That(GetPrivateField<long>(provider, "_propagationStartTimestampNs"),
                Is.EqualTo(1_000_000_000L));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(gameObject);
        }
    }

    [Test]
    public void ReacquisitionHandover_SecondCleanCandidateCanReturnAuthority()
    {
        Assert.That(ReacquisitionReady(
            hasHighConfidenceVisualPose: true,
            elapsedMilliseconds: 300,
            positionDifferenceMeters: 0.6f,
            rotationDifferenceDegrees: 3f), Is.True);
    }

    [Test]
    public void FreshRelocalization_FirstHighConfidenceFrameStaysNoneDuringDwell()
    {
        Assert.That(FreshRelocalizationReady(
            hasHighConfidenceVisualPose: true,
            elapsedMilliseconds: 0), Is.False);
    }

    [Test]
    public void FreshRelocalization_StableFramesShorterThanDwellStayNone()
    {
        Assert.That(FreshRelocalizationReady(
            hasHighConfidenceVisualPose: true,
            elapsedMilliseconds: 200), Is.False);
    }

    [Test]
    public void FreshRelocalization_StableFramesAfterDwellCanEstablishNewVisualSession()
    {
        Assert.That(FreshRelocalizationReady(
            hasHighConfidenceVisualPose: true,
            elapsedMilliseconds: 300), Is.True);
    }

    [Test]
    public void FreshRelocalization_IgnoresExpiredTwoMeterSpatialDisagreement()
    {
        Assert.That(FreshRelocalizationReady(
            hasHighConfidenceVisualPose: true,
            elapsedMilliseconds: 300), Is.True);
        Assert.That(ReacquisitionReady(
            hasHighConfidenceVisualPose: true,
            elapsedMilliseconds: 300,
            positionDifferenceMeters: 2f,
            rotationDifferenceDegrees: 1f), Is.False);
    }

    [Test]
    public void FreshRelocalization_QualityFailureCannotEstablishVisualAuthority()
    {
        Assert.That(FreshRelocalizationReady(
            hasHighConfidenceVisualPose: false,
            elapsedMilliseconds: 300), Is.False);
    }

    [TestCase(0L, false)]
    [TestCase(100L, false)]
    [TestCase(249L, false)]
    [TestCase(250L, true)]
    [TestCase(251L, true)]
    public void FreshRelocalization_DwellBoundaryIsExact(long elapsedMilliseconds, bool expectedReady)
    {
        Assert.That(FreshRelocalizationReady(
            hasHighConfidenceVisualPose: true,
            elapsedMilliseconds: elapsedMilliseconds), Is.EqualTo(expectedReady));
    }

    [Test]
    public void FreshRelocalization_NewSessionStillRejectsARealLaterFrameJump()
    {
        // The first frame after NONE has no predecessor and therefore no
        // cross-session jump. Once it establishes the new sequence, ordinary
        // per-frame jump protection remains active.
        var firstFrame = DjiVisualPoseQualityPolicy.EvaluateMotionAwareTranslationJump(
            translationJumpMeters: 0f,
            elapsedSeconds: 0f,
            hasRotationPrediction: false,
            rotationCompensatedResidualMeters: float.NaN,
            hasTelemetrySpeed: false,
            telemetrySpeedMetersPerSecond: float.NaN,
            maximumNormalJumpMeters: 0.035f,
            immediateRejectJumpMeters: 0.15f,
            motionResidualAllowanceMeters: 0.015f,
            telemetrySpeedAllowanceMultiplier: 1.5f);
        var laterFrame = DjiVisualPoseQualityPolicy.EvaluateMotionAwareTranslationJump(
            translationJumpMeters: 0.2f,
            elapsedSeconds: 0.1f,
            hasRotationPrediction: true,
            rotationCompensatedResidualMeters: 0.01f,
            hasTelemetrySpeed: true,
            telemetrySpeedMetersPerSecond: 1f,
            maximumNormalJumpMeters: 0.035f,
            immediateRejectJumpMeters: 0.15f,
            motionResidualAllowanceMeters: 0.015f,
            telemetrySpeedAllowanceMultiplier: 1.5f);

        Assert.That(firstFrame.decision, Is.EqualTo(VisualTranslationJumpDecision.Accept));
        Assert.That(laterFrame.decision, Is.EqualTo(VisualTranslationJumpDecision.Reject));
    }

    [Test]
    public void TimeoutReset_ClearsStaleLocalizationAndGuardedHorizontalState()
    {
        var gameObject = new GameObject("FreshRelocalizationTimeoutResetTest");
        try
        {
            var provider = gameObject.AddComponent<DjiBoardVisionProvider>();
            PrimeExpiredLocalizationState(provider);

            InvokePrivate(provider, "ExpireLocalizationSessionAfterPropagationTimeout");

            Assert.That(GetPrivateField<bool>(provider, "_hasShadowRotationPropagationBaseline"), Is.False);
            Assert.That(GetPrivateField<bool>(provider, "_shadowPropagationActive"), Is.False);
            Assert.That(GetPrivateField<long>(provider, "_propagationStartTimestampNs"), Is.EqualTo(0L));
            Assert.That(GetPrivateField<long>(provider, "_translationShadowStartTimestampNs"), Is.EqualTo(0L));
            Assert.That(GetPrivateField<bool>(provider, "_hasPendingReacquisition"), Is.False);
            Assert.That(GetPrivateField<long>(provider, "_pendingReacquisitionStartTimestampNs"), Is.EqualTo(0L));
            Assert.That(GetPrivateField<bool>(provider, "_hasLastVisualQualityPose"), Is.False);
            Assert.That(GetPrivateField<long>(provider, "_lastVisualQualityPoseTimestampNs"), Is.EqualTo(0L));
            Assert.That(GetPrivateField<bool>(provider, "_hasLastVisualQualityTelemetryRotation"), Is.False);
            Assert.That(GetPrivateField<bool>(provider, "_hasVisualReacquisitionCandidate"), Is.False);
            Assert.That(GetPrivateField<int>(provider, "_consecutiveTranslationJumpSuspects"), Is.Zero);
            Assert.That(GetPrivateField<int>(provider, "_consecutiveBorderlineFourMarkerQualityFailures"), Is.Zero);
            Assert.That(provider.HasHighConfidenceCameraFromBoard, Is.False);
            Assert.That(provider.CurrentVisualPoseQuality, Is.EqualTo(DjiBoardVisionProvider.VisualPoseQuality.Unavailable));
            Assert.That(GetPrivateField<object>(provider, "_visualReacquisitionMode").ToString(), Is.EqualTo("FreshRelocalization"));

            Assert.That(provider.TryGetGuardedHorizontalRenderSnapshot(out var guarded), Is.True);
            Assert.That(guarded.state, Is.EqualTo(GuardedHorizontalTranslationState.Off));
            Assert.That(guarded.correctionInNavigation, Is.EqualTo(Vector2.zero));
            Assert.That(guarded.appliedCorrectionInNavigation, Is.EqualTo(Vector2.zero));
            Assert.That(guarded.blendAlpha, Is.Zero);
            Assert.That(guarded.consistentSampleCount, Is.Zero);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(gameObject);
        }
    }

    [Test]
    public void TimeoutReset_RepeatedCyclesDoNotAccumulateSessionState()
    {
        var gameObject = new GameObject("FreshRelocalizationRepeatedTimeoutTest");
        try
        {
            var provider = gameObject.AddComponent<DjiBoardVisionProvider>();
            for (var cycle = 0; cycle < 5; cycle++)
            {
                PrimeExpiredLocalizationState(provider);
                InvokePrivate(provider, "ExpireLocalizationSessionAfterPropagationTimeout");

                Assert.That(GetPrivateField<bool>(provider, "_hasShadowRotationPropagationBaseline"), Is.False);
                Assert.That(GetPrivateField<bool>(provider, "_hasLastVisualQualityPose"), Is.False);
                Assert.That(GetPrivateField<int>(provider, "_consecutiveTranslationJumpSuspects"), Is.Zero);
                Assert.That(GetPrivateField<int>(provider, "_consecutiveBorderlineFourMarkerQualityFailures"), Is.Zero);
            }
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(gameObject);
        }
    }

    [Test]
    public void HorizontalRenderMode_DefaultsToShadowOnlyWithInitialSegmentZero()
    {
        var gameObject = new GameObject("HorizontalRenderModeDefaultTest");
        try
        {
            var provider = gameObject.AddComponent<DjiBoardVisionProvider>();

            Assert.That(
                provider.CurrentHorizontalTranslationRenderMode,
                Is.EqualTo(HorizontalTranslationRenderMode.ShadowOnly));
            Assert.That(provider.HorizontalModeSegmentId, Is.Zero);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(gameObject);
        }
    }

    [Test]
    public void HorizontalRenderMode_ToggleResetsGuardedCorrectionWithoutClearingVisualPose()
    {
        var gameObject = new GameObject("HorizontalRenderModeVisualToggleTest");
        try
        {
            var provider = gameObject.AddComponent<DjiBoardVisionProvider>();
            PrimeExpiredLocalizationState(provider);
            var visualPose = new Pose(new Vector3(1f, 2f, 3f), Quaternion.Euler(4f, 5f, 6f));
            SetPrivateProperty(provider, "CurrentHighConfidenceCameraFromBoard", visualPose);
            SetPrivateProperty(provider, "HasHighConfidenceCameraFromBoard", true);
            SetPrivateProperty(provider, "CurrentRenderPoseSource", DjiRenderPoseSource.Visual);

            Assert.That(provider.SetHorizontalTranslationRenderMode(HorizontalTranslationRenderMode.GuardedGnssRendered), Is.True);
            Assert.That(provider.SetHorizontalTranslationRenderMode(HorizontalTranslationRenderMode.ShadowOnly), Is.True);
            Assert.That(provider.CurrentHorizontalTranslationRenderMode, Is.EqualTo(HorizontalTranslationRenderMode.ShadowOnly));
            Assert.That(provider.HorizontalModeSegmentId, Is.EqualTo(2));
            Assert.That(provider.CurrentRenderPoseSource, Is.EqualTo(DjiRenderPoseSource.Visual));
            Assert.That(provider.CurrentHighConfidenceCameraFromBoard.position, Is.EqualTo(visualPose.position));
            Assert.That(provider.TryGetGuardedHorizontalRenderSnapshot(out var snapshot), Is.True);
            Assert.That(snapshot.renderMode, Is.EqualTo(HorizontalTranslationRenderMode.ShadowOnly));
            Assert.That(snapshot.state, Is.EqualTo(GuardedHorizontalTranslationState.Off));
            Assert.That(snapshot.appliedCorrectionInNavigation, Is.EqualTo(Vector2.zero));
            Assert.That(snapshot.blendAlpha, Is.Zero);
            Assert.That(snapshot.consistentSampleCount, Is.Zero);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(gameObject);
        }
    }

    [Test]
    public void HorizontalRenderMode_ActualChangesIncrementSegmentButDuplicateSelectionDoesNotReset()
    {
        var gameObject = new GameObject("HorizontalRenderModeSegmentTest");
        try
        {
            var provider = gameObject.AddComponent<DjiBoardVisionProvider>();

            Assert.That(provider.SetHorizontalTranslationRenderMode(HorizontalTranslationRenderMode.GuardedGnssRendered), Is.True);
            Assert.That(provider.HorizontalModeSegmentId, Is.EqualTo(1));
            Assert.That(provider.SetHorizontalTranslationRenderMode(HorizontalTranslationRenderMode.ShadowOnly), Is.True);
            Assert.That(provider.HorizontalModeSegmentId, Is.EqualTo(2));
            Assert.That(provider.SetHorizontalTranslationRenderMode(HorizontalTranslationRenderMode.ShadowOnly), Is.False);
            Assert.That(provider.HorizontalModeSegmentId, Is.EqualTo(2));
            Assert.That(provider.SetHorizontalTranslationRenderMode(HorizontalTranslationRenderMode.GuardedGnssRendered), Is.True);
            Assert.That(provider.HorizontalModeSegmentId, Is.EqualTo(3));
            Assert.That(provider.TryGetGuardedHorizontalRenderSnapshot(out var snapshot), Is.True);
            Assert.That(snapshot.renderMode, Is.EqualTo(HorizontalTranslationRenderMode.GuardedGnssRendered));
            Assert.That(snapshot.state, Is.EqualTo(GuardedHorizontalTranslationState.Off));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(gameObject);
        }
    }

    [Test]
    public void HorizontalRenderMode_ToggleDuringPropagationOrNoneDoesNotChangeRenderAuthority()
    {
        var gameObject = new GameObject("HorizontalRenderModeAuthorityTest");
        try
        {
            var provider = gameObject.AddComponent<DjiBoardVisionProvider>();
            SetPrivateProperty(provider, "CurrentRenderPoseSource", DjiRenderPoseSource.TelemetryPropagated);

            provider.SetHorizontalTranslationRenderMode(HorizontalTranslationRenderMode.ShadowOnly);
            Assert.That(provider.CurrentRenderPoseSource, Is.EqualTo(DjiRenderPoseSource.TelemetryPropagated));

            SetPrivateProperty(provider, "CurrentRenderPoseSource", DjiRenderPoseSource.None);
            provider.SetHorizontalTranslationRenderMode(HorizontalTranslationRenderMode.GuardedGnssRendered);
            Assert.That(provider.CurrentRenderPoseSource, Is.EqualTo(DjiRenderPoseSource.None));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(gameObject);
        }
    }

    [Test]
    public void HorizontalRenderMode_ToggleAfterTimeoutCannotRestoreExpiredLocalizationState()
    {
        var gameObject = new GameObject("HorizontalRenderModeTimeoutTest");
        try
        {
            var provider = gameObject.AddComponent<DjiBoardVisionProvider>();
            PrimeExpiredLocalizationState(provider);
            InvokePrivate(provider, "ExpireLocalizationSessionAfterPropagationTimeout");

            provider.SetHorizontalTranslationRenderMode(HorizontalTranslationRenderMode.ShadowOnly);

            Assert.That(GetPrivateField<bool>(provider, "_hasShadowRotationPropagationBaseline"), Is.False);
            Assert.That(GetPrivateField<bool>(provider, "_hasLastVisualQualityPose"), Is.False);
            Assert.That(GetPrivateField<object>(provider, "_visualReacquisitionMode").ToString(), Is.EqualTo("FreshRelocalization"));
            Assert.That(provider.CurrentRenderPoseSource, Is.EqualTo(DjiRenderPoseSource.None));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(gameObject);
        }
    }

    [Test]
    public void HorizontalRenderMode_ToggleDoesNotModifyGuardedPolicyConstants()
    {
        var gameObject = new GameObject("HorizontalRenderModePolicyConstantsTest");
        try
        {
            var provider = gameObject.AddComponent<DjiBoardVisionProvider>();
            var configuration = GetPrivateField<GuardedHorizontalRenderConfiguration>(
                provider,
                "guardedHorizontalRenderConfiguration");
            var expectedMinAge = configuration.minPropagationAgeMilliseconds;
            var expectedMaxAge = configuration.maxPropagationAgeMilliseconds;
            var expectedGnssAge = configuration.maxGnssAgeMilliseconds;
            var expectedEnable = configuration.horizontalEnableThresholdMeters;
            var expectedDisable = configuration.horizontalDisableThresholdMeters;
            var expectedSamples = configuration.requiredConsistentGnssSamples;
            var expectedDirection = configuration.maxConsistencyDirectionDifferenceDegrees;
            var expectedActivationBlend = configuration.activationBlendMilliseconds;
            var expectedGrace = configuration.gnssLossGraceMilliseconds;
            var expectedDeactivationBlend = configuration.deactivationBlendMilliseconds;

            provider.ToggleHorizontalTranslationRenderMode();
            provider.ToggleHorizontalTranslationRenderMode();

            Assert.That(configuration.minPropagationAgeMilliseconds, Is.EqualTo(expectedMinAge));
            Assert.That(configuration.maxPropagationAgeMilliseconds, Is.EqualTo(expectedMaxAge));
            Assert.That(configuration.maxGnssAgeMilliseconds, Is.EqualTo(expectedGnssAge));
            Assert.That(configuration.horizontalEnableThresholdMeters, Is.EqualTo(expectedEnable));
            Assert.That(configuration.horizontalDisableThresholdMeters, Is.EqualTo(expectedDisable));
            Assert.That(configuration.requiredConsistentGnssSamples, Is.EqualTo(expectedSamples));
            Assert.That(configuration.maxConsistencyDirectionDifferenceDegrees, Is.EqualTo(expectedDirection));
            Assert.That(configuration.activationBlendMilliseconds, Is.EqualTo(expectedActivationBlend));
            Assert.That(configuration.gnssLossGraceMilliseconds, Is.EqualTo(expectedGrace));
            Assert.That(configuration.deactivationBlendMilliseconds, Is.EqualTo(expectedDeactivationBlend));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(gameObject);
        }
    }

    [Test]
    public void RenderPolicy_ReturnsNoneAfterPropagationTimeout()
    {
        var source = RotationPropagationRenderPolicy.Resolve(
            hasVisualPose: true,
            visualPoseAgeSeconds: 3f,
            visualPoseFreshnessSeconds: 2f,
            rotationPropagationEnabled: true,
            hasPredictedPose: true,
            propagationDurationSeconds: 2.1f,
            maximumPropagationDurationSeconds: 2f);

        Assert.That(source, Is.EqualTo(DjiRenderPoseSource.None));
    }

    [TestCase(1.999f, false, DjiRenderPoseSource.TelemetryPropagated)]
    [TestCase(2.000f, false, DjiRenderPoseSource.TelemetryPropagated)]
    [TestCase(2.001f, false, DjiRenderPoseSource.None)]
    [TestCase(1.999f, true, DjiRenderPoseSource.TelemetryPropagated)]
    [TestCase(2.000f, true, DjiRenderPoseSource.TelemetryPropagated)]
    [TestCase(2.001f, true, DjiRenderPoseSource.TelemetryPropagated)]
    public void RenderPolicy_ChangesContinuationRequirementsButNotAuthorityAtBoundary(
        float ageSeconds, bool hasLongLivedTelemetry, DjiRenderPoseSource expected)
    {
        var source = RotationPropagationRenderPolicy.Resolve(
            hasVisualPose: false,
            visualPoseAgeSeconds: 3f,
            visualPoseFreshnessSeconds: 2f,
            rotationPropagationEnabled: true,
            hasPredictedPose: true,
            propagationDurationSeconds: ageSeconds,
            maximumPropagationDurationSeconds: 2f,
            hasLongLivedTelemetryLocalization: hasLongLivedTelemetry);

        Assert.That(source, Is.EqualTo(expected));
    }

    [Test]
    public void RenderPolicy_LongLivedTelemetryUnavailableStillReturnsNoneAfterRotationOnlyWindow()
    {
        var source = RotationPropagationRenderPolicy.Resolve(
            hasVisualPose: false,
            visualPoseAgeSeconds: 3f,
            visualPoseFreshnessSeconds: 2f,
            rotationPropagationEnabled: true,
            hasPredictedPose: true,
            propagationDurationSeconds: 2.001f,
            maximumPropagationDurationSeconds: 2f,
            hasLongLivedTelemetryLocalization: false);

        Assert.That(source, Is.EqualTo(DjiRenderPoseSource.None));
    }

    [Test]
    public void RenderPolicy_UsesFreshRenderTimerInsteadOfOlderPredictionBaseline()
    {
        const long baselineTimestampNs = 1_000_000_000L;
        const long propagationTransitionTimestampNs = 3_100_000_000L;
        var propagationStartTimestampNs = RotationPropagationTiming.StartIfNeeded(
            RotationPropagationTiming.NoTimestamp,
            propagationTransitionTimestampNs);

        var source = RotationPropagationRenderPolicy.Resolve(
            hasVisualPose: true,
            visualPoseAgeSeconds: 2.1f,
            visualPoseFreshnessSeconds: 2f,
            rotationPropagationEnabled: true,
            hasPredictedPose: true,
            propagationDurationSeconds: RotationPropagationTiming.AgeSeconds(
                propagationTransitionTimestampNs,
                propagationStartTimestampNs),
            maximumPropagationDurationSeconds: 2f);

        Assert.That(
            (propagationTransitionTimestampNs - baselineTimestampNs) / 1_000_000.0,
            Is.EqualTo(2100d));
        Assert.That(RotationPropagationTiming.AgeSeconds(
            propagationTransitionTimestampNs,
            propagationStartTimestampNs), Is.EqualTo(0f).Within(0.0001f));
        Assert.That(source, Is.EqualTo(DjiRenderPoseSource.TelemetryPropagated));
    }

    [TestCase(1000L)]
    [TestCase(1990L)]
    public void RenderPolicy_KeepsTelemetryPropagationActiveWithinShortWindow(long propagationAgeMilliseconds)
    {
        var source = RotationPropagationRenderPolicy.Resolve(
            hasVisualPose: true,
            visualPoseAgeSeconds: 3f,
            visualPoseFreshnessSeconds: 2f,
            rotationPropagationEnabled: true,
            hasPredictedPose: true,
            propagationDurationSeconds: propagationAgeMilliseconds / 1000f,
            maximumPropagationDurationSeconds: 2f);

        Assert.That(source, Is.EqualTo(DjiRenderPoseSource.TelemetryPropagated));
    }

    [Test]
    public void RenderTimer_ReacquisitionResetsAndSecondBoardLossStartsFresh()
    {
        const long firstTransitionTimestampNs = 10_000_000_000L;
        var firstStartTimestampNs = RotationPropagationTiming.StartIfNeeded(
            RotationPropagationTiming.NoTimestamp,
            firstTransitionTimestampNs);

        Assert.That(RotationPropagationTiming.AgeSeconds(
            firstTransitionTimestampNs + 1_000_000_000L,
            firstStartTimestampNs), Is.EqualTo(1f).Within(0.0001f));

        var resetTimestampNs = RotationPropagationTiming.Reset();
        const long secondTransitionTimestampNs = 20_000_000_000L;
        var secondStartTimestampNs = RotationPropagationTiming.StartIfNeeded(
            resetTimestampNs,
            secondTransitionTimestampNs);

        Assert.That(resetTimestampNs, Is.EqualTo(RotationPropagationTiming.NoTimestamp));
        Assert.That(secondStartTimestampNs, Is.EqualTo(secondTransitionTimestampNs));
        Assert.That(RotationPropagationTiming.AgeSeconds(
            secondTransitionTimestampNs,
            secondStartTimestampNs), Is.EqualTo(0f).Within(0.0001f));
    }

    [Test]
    public void RenderPolicy_BlocksUnsafeEnabledModeWithoutOverride()
    {
        Assert.That(RotationPropagationRenderPolicy.IsEnabledAllowed(
            DjiBoardVisionProvider.RotationPropagationMode.Enabled,
            safeForRotationOnlyPropagation: false,
            developerOverride: false), Is.False);
    }

    [Test]
    public void RenderPolicy_AllowsValidatedEnabledModeWithoutDeveloperOverride()
    {
        Assert.That(RotationPropagationRenderPolicy.IsEnabledAllowed(
            DjiBoardVisionProvider.RotationPropagationMode.Enabled,
            safeForRotationOnlyPropagation: true,
            developerOverride: false), Is.True);
    }

    [Test]
    public void RenderPolicy_AllowsDeveloperOverrideForEnabledMode()
    {
        Assert.That(RotationPropagationRenderPolicy.IsEnabledAllowed(
            DjiBoardVisionProvider.RotationPropagationMode.Enabled,
            safeForRotationOnlyPropagation: false,
            developerOverride: true), Is.True);
    }

    [Test]
    public void RotationOnlyPrediction_RotatesBothBoardRotationAndTranslation()
    {
        var camera0FromBoard = new Pose(
            new Vector3(1f, 2f, 3f),
            Quaternion.AngleAxis(20f, Vector3.right));
        var navigationFromCamera0 = Quaternion.identity;
        var navigationFromCameraT = Quaternion.AngleAxis(90f, Vector3.up);

        var predicted = ReferenceFrameTransforms.PredictCameraFromBoardRotationOnly(
            camera0FromBoard,
            navigationFromCamera0,
            navigationFromCameraT);
        var expectedCameraTFromCamera0 = Quaternion.Inverse(navigationFromCameraT);

        Assert.That(
            Vector3.Distance(predicted.position, expectedCameraTFromCamera0 * camera0FromBoard.position),
            Is.LessThan(0.0001f));
        Assert.That(
            Quaternion.Angle(predicted.rotation, expectedCameraTFromCamera0 * camera0FromBoard.rotation),
            Is.LessThan(0.001f));
    }

    [Test]
    public void VelocityHistory_EnforcesMonotonicBoundedInsertionAndAcceptsRepeatedValues()
    {
        var history = new TelemetryVelocityHistory(capacity: 2);
        var repeatedVelocity = new Vector3(0.25f, -0.5f, 0.75f);

        Assert.That(history.TryAdd(new DjiCameraPoseProvider.TelemetryVelocitySample(100L, repeatedVelocity), out var firstReason), Is.True);
        Assert.That(firstReason, Is.Empty);
        Assert.That(history.TryAdd(new DjiCameraPoseProvider.TelemetryVelocitySample(200L, repeatedVelocity), out _), Is.True);
        Assert.That(history.TryAdd(new DjiCameraPoseProvider.TelemetryVelocitySample(300L, Vector3.zero), out _), Is.True);

        Assert.That(history.Count, Is.EqualTo(2));
        Assert.That(history.OldestTimestampNs, Is.EqualTo(200L));
        Assert.That(history.NewestTimestampNs, Is.EqualTo(300L));
        Assert.That(history.TryAdd(new DjiCameraPoseProvider.TelemetryVelocitySample(300L, Vector3.one), out var duplicateReason), Is.False);
        Assert.That(duplicateReason, Is.EqualTo("DUPLICATE_TIMESTAMP"));
        Assert.That(history.TryAdd(new DjiCameraPoseProvider.TelemetryVelocitySample(250L, Vector3.one), out var backwardReason), Is.False);
        Assert.That(backwardReason, Is.EqualTo("NON_MONOTONIC_TIMESTAMP"));
        Assert.That(history.TryAdd(new DjiCameraPoseProvider.TelemetryVelocitySample(0L, Vector3.one), out var zeroReason), Is.False);
        Assert.That(zeroReason, Is.EqualTo("ZERO_TIMESTAMP"));
    }

    [Test]
    public void VelocityHistory_SyncBridgeHistorySkipsKnownReplayAndAppendsOnlyNewSamples()
    {
        var history = new TelemetryVelocityHistory(capacity: 8);
        Assert.That(history.TryAdd(new DjiCameraPoseProvider.TelemetryVelocitySample(100L, Vector3.zero), out _), Is.True);
        Assert.That(history.TryAdd(new DjiCameraPoseProvider.TelemetryVelocitySample(200L, Vector3.one), out _), Is.True);
        Assert.That(history.TryAdd(new DjiCameraPoseProvider.TelemetryVelocitySample(300L, Vector3.right), out _), Is.True);

        var sync = history.SyncBridgeSamples(new[]
        {
            new DjiCameraPoseProvider.TelemetryVelocitySample(100L, Vector3.zero),
            new DjiCameraPoseProvider.TelemetryVelocitySample(200L, Vector3.one),
            new DjiCameraPoseProvider.TelemetryVelocitySample(300L, Vector3.right),
            new DjiCameraPoseProvider.TelemetryVelocitySample(400L, Vector3.up),
            new DjiCameraPoseProvider.TelemetryVelocitySample(500L, Vector3.forward)
        });

        Assert.That(sync.received, Is.EqualTo(5));
        Assert.That(sync.skippedAlreadyKnown, Is.EqualTo(3));
        Assert.That(sync.appendedNew, Is.EqualTo(2));
        Assert.That(sync.rejectedInvalid, Is.Zero);
        Assert.That(history.Count, Is.EqualTo(5));
        Assert.That(history.NewestTimestampNs, Is.EqualTo(500L));
    }

    [Test]
    public void VelocityHistory_SyncAcceptsIdenticalVelocityWhenTimestampIsNewer()
    {
        var history = new TelemetryVelocityHistory(capacity: 4);
        var repeatedVelocity = new Vector3(0.25f, -0.5f, 0.75f);
        Assert.That(history.TryAdd(new DjiCameraPoseProvider.TelemetryVelocitySample(100L, repeatedVelocity), out _), Is.True);

        var sync = history.SyncBridgeSamples(new[]
        {
            new DjiCameraPoseProvider.TelemetryVelocitySample(100L, repeatedVelocity),
            new DjiCameraPoseProvider.TelemetryVelocitySample(200L, repeatedVelocity)
        });

        Assert.That(sync.skippedAlreadyKnown, Is.EqualTo(1));
        Assert.That(sync.appendedNew, Is.EqualTo(1));
        Assert.That(sync.rejectedInvalid, Is.Zero);
        Assert.That(history.Count, Is.EqualTo(2));
        Assert.That(history.NewestTimestampNs, Is.EqualTo(200L));
    }

    [Test]
    public void VelocityHistory_InterpolatesNedComponentsInsideValidBracket()
    {
        var history = new TelemetryVelocityHistory(capacity: 4);
        Assert.That(history.TryAdd(new DjiCameraPoseProvider.TelemetryVelocitySample(1_000L, new Vector3(0f, 2f, 4f)), out _), Is.True);
        Assert.That(history.TryAdd(new DjiCameraPoseProvider.TelemetryVelocitySample(3_000L, new Vector3(2f, 4f, 8f)), out _), Is.True);

        Assert.That(history.TryInterpolate(2_000L, maximumBracketGapNs: 3_000L, out var interpolated, out var reason), Is.True);
        Assert.That(reason, Is.Empty);
        Assert.That(Vector3.Distance(interpolated, new Vector3(1f, 3f, 6f)), Is.LessThan(0.0001f));
    }

    [Test]
    public void VelocityHistory_RejectsOutOfRangeAndExcessiveGapLookups()
    {
        var history = new TelemetryVelocityHistory(capacity: 4);
        Assert.That(history.TryAdd(new DjiCameraPoseProvider.TelemetryVelocitySample(1_000L, Vector3.zero), out _), Is.True);
        Assert.That(history.TryAdd(new DjiCameraPoseProvider.TelemetryVelocitySample(11_000L, Vector3.one), out _), Is.True);

        Assert.That(history.TryInterpolate(500L, 20_000L, out _, out var outsideReason), Is.False);
        Assert.That(outsideReason, Is.EqualTo("BRACKET_UNAVAILABLE"));
        Assert.That(history.TryInterpolate(6_000L, 5_000L, out _, out var wideGapReason), Is.False);
        Assert.That(wideGapReason, Is.EqualTo("BRACKET_GAP_TOO_LARGE"));
    }

    [Test]
    public void VelocityTranslationShadow_StationaryVelocityMatchesRotationOnlyPrediction()
    {
        var history = new TelemetryVelocityHistory(capacity: 4);
        Assert.That(history.TryAdd(new DjiCameraPoseProvider.TelemetryVelocitySample(1_000_000_000L, Vector3.zero), out _), Is.True);
        Assert.That(history.TryAdd(new DjiCameraPoseProvider.TelemetryVelocitySample(2_000_000_000L, Vector3.zero), out _), Is.True);
        Assert.That(ReferenceFrameTransforms.TryIntegrateNavigationVelocityTrapezoidal(
            history.Samples,
            1_000_000_000L,
            Vector3.zero,
            2_000_000_000L,
            Vector3.zero,
            500_000_000L + 500_000_000L,
            out var displacement,
            out var reason), Is.True);
        Assert.That(reason, Is.Empty);
        Assert.That(displacement, Is.EqualTo(Vector3.zero));

        var baseline = new Pose(new Vector3(1f, 2f, 3f), Quaternion.identity);
        var rotationOnly = ReferenceFrameTransforms.PredictCameraFromBoardRotationOnly(
            baseline, Quaternion.identity, Quaternion.identity);
        var velocityShadow = ReferenceFrameTransforms.PredictCameraFromBoardWithNavigationTranslation(
            baseline, Quaternion.identity, Quaternion.identity, displacement);
        Assert.That(Vector3.Distance(rotationOnly.position, velocityShadow.position), Is.LessThan(0.0001f));
        Assert.That(Quaternion.Angle(rotationOnly.rotation, velocityShadow.rotation), Is.LessThan(0.001f));
    }

    [Test]
    public void VisualCameraDisplacementInNavigation_UsesStationaryBoardPositionDifference()
    {
        var displacement = ReferenceFrameTransforms.CameraDisplacementInNavigationFromStationaryBoard(
            new Pose(Vector3.zero, Quaternion.identity),
            Quaternion.identity,
            new Pose(new Vector3(-0.5f, -0.25f, -0.75f), Quaternion.identity),
            Quaternion.identity);

        // With an identity camera basis, a board that becomes negative in
        // camera coordinates means the camera moved positive N/E/D.
        Assert.That(Vector3.Distance(displacement, new Vector3(0.5f, 0.25f, 0.75f)), Is.LessThan(0.0001f));
    }

    [Test]
    public void VisualCameraDisplacementInNavigation_RemainsCorrectAcrossCameraRotation()
    {
        var navigationFromCamera1 = Quaternion.AngleAxis(90f, Vector3.forward);
        var displacement = ReferenceFrameTransforms.CameraDisplacementInNavigationFromStationaryBoard(
            new Pose(new Vector3(2f, 0f, 0f), Quaternion.identity),
            Quaternion.identity,
            new Pose(new Vector3(0f, -1f, 0f), Quaternion.identity),
            navigationFromCamera1);

        // Both visual poses observe the same stationary board. The camera
        // moved one metre north while its basis also rotated by +90 degrees.
        Assert.That(Vector3.Distance(displacement, Vector3.right), Is.LessThan(0.0001f));
    }

    [Test]
    public void VelocityTranslationShadow_NorthMotionUsesNegativeCameraRelativeBoardSign()
    {
        var velocity = new Vector3(1f, 0f, 0f);
        Assert.That(ReferenceFrameTransforms.TryIntegrateNavigationVelocityTrapezoidal(
            new[]
            {
                new DjiCameraPoseProvider.TelemetryVelocitySample(1_000_000_000L, velocity),
                new DjiCameraPoseProvider.TelemetryVelocitySample(2_000_000_000L, velocity)
            },
            1_000_000_000L, velocity,
            2_000_000_000L, velocity,
            1_000_000_000L,
            out var displacement,
            out _), Is.True);
        Assert.That(Vector3.Distance(displacement, new Vector3(1f, 0f, 0f)), Is.LessThan(0.0001f));

        var prediction = ReferenceFrameTransforms.PredictCameraFromBoardWithNavigationTranslation(
            new Pose(Vector3.zero, Quaternion.identity),
            Quaternion.identity,
            Quaternion.identity,
            displacement);
        Assert.That(Vector3.Distance(prediction.position, new Vector3(-1f, 0f, 0f)), Is.LessThan(0.0001f));
    }

    [Test]
    public void HorizontalGnssShadow_UsesTheSameStationaryBoardSignWithoutVerticalMotion()
    {
        // The GNSS path supplies only validated N/E displacement. It must use
        // the common transform helper so a north/east camera movement moves
        // the stationary board in the opposite camera-relative direction.
        var prediction = ReferenceFrameTransforms.PredictCameraFromBoardWithNavigationTranslation(
            new Pose(Vector3.zero, Quaternion.identity),
            Quaternion.identity,
            Quaternion.identity,
            new Vector3(0.4f, -0.2f, 0f));

        Assert.That(Vector3.Distance(prediction.position, new Vector3(-0.4f, 0.2f, 0f)), Is.LessThan(0.0001f));
        Assert.That(prediction.position.z, Is.EqualTo(0f).Within(0.0001f));
    }

    [Test]
    public void HybridHorizontalShadow_SelectsFreshComplementarySourcesWithoutDoubleCounting()
    {
        Assert.That(
            ReferenceFrameTransforms.ResolveHybridHorizontalShadowMode(
                hasGnssDisplacement: true,
                hasFreshVelocityAfterGnss: true,
                hasFreshVelocityFromBaseline: true),
            Is.EqualTo(ReferenceFrameTransforms.HybridHorizontalShadowMode.GnssPlusVelocity));
        Assert.That(
            ReferenceFrameTransforms.ResolveHybridHorizontalShadowMode(true, false, true),
            Is.EqualTo(ReferenceFrameTransforms.HybridHorizontalShadowMode.GnssOnly));
        Assert.That(
            ReferenceFrameTransforms.ResolveHybridHorizontalShadowMode(false, false, true),
            Is.EqualTo(ReferenceFrameTransforms.HybridHorizontalShadowMode.VelocityOnly));
        Assert.That(
            ReferenceFrameTransforms.ResolveHybridHorizontalShadowMode(false, false, false),
            Is.EqualTo(ReferenceFrameTransforms.HybridHorizontalShadowMode.RotationOnlyFallback));

        var combined = ReferenceFrameTransforms.CombineHorizontalGnssAndVelocityIncrement(
            new Vector3(1f, 2f, 0f),
            new Vector3(0.1f, -0.2f, 5f));
        Assert.That(Vector3.Distance(combined, new Vector3(1.1f, 1.8f, 0f)), Is.LessThan(0.0001f));
    }

    [Test]
    public void HybridHorizontalShadow_StalePostGnssVelocityUsesGnssOnly()
    {
        var history = new[]
        {
            new DjiCameraPoseProvider.TelemetryVelocitySample(1_000_000_000L, Vector3.right)
        };
        Assert.That(ReferenceFrameTransforms.TryIntegrateNavigationVelocityStateHistory(
            history,
            startTimestampNs: 1_000_000_000L,
            endTimestampNs: 2_000_000_000L,
            maximumSampleAgeNs: 500_000_000L,
            out _,
            out _,
            out var reason), Is.False);
        Assert.That(reason, Is.EqualTo("TARGET_STALE"));
        Assert.That(
            ReferenceFrameTransforms.ResolveHybridHorizontalShadowMode(true, false, false),
            Is.EqualTo(ReferenceFrameTransforms.HybridHorizontalShadowMode.GnssOnly));
    }

    [Test]
    public void HybridHorizontalShadow_UsesNeutralMarginForBestSourceClassification()
    {
        Assert.That(
            RotationPropagationRenderPolicy.SelectBestHorizontalSource(
                rotationOnlyErrorMeters: 0.20f,
                freshVelocityErrorMeters: float.NaN,
                gnssErrorMeters: 0.10f,
                hybridErrorMeters: 0.06f,
                neutralMarginMeters: 0.03f),
            Is.EqualTo(HybridHorizontalBestSource.Hybrid));
        Assert.That(
            RotationPropagationRenderPolicy.SelectBestHorizontalSource(
                rotationOnlyErrorMeters: 0.10f,
                freshVelocityErrorMeters: float.NaN,
                gnssErrorMeters: 0.085f,
                hybridErrorMeters: float.NaN,
                neutralMarginMeters: 0.03f),
            Is.EqualTo(HybridHorizontalBestSource.Similar));
    }

    [Test]
    public void VelocityTranslationShadow_EastAndDownMotionPreserveNedAxes()
    {
        var eastAndDown = new Vector3(0f, 1f, 1f);
        Assert.That(ReferenceFrameTransforms.TryIntegrateNavigationVelocityTrapezoidal(
            new[]
            {
                new DjiCameraPoseProvider.TelemetryVelocitySample(1_000_000_000L, eastAndDown),
                new DjiCameraPoseProvider.TelemetryVelocitySample(2_000_000_000L, eastAndDown)
            },
            1_000_000_000L, eastAndDown,
            2_000_000_000L, eastAndDown,
            1_000_000_000L,
            out var displacement,
            out _), Is.True);
        Assert.That(Vector3.Distance(displacement, new Vector3(0f, 1f, 1f)), Is.LessThan(0.0001f));

        var prediction = ReferenceFrameTransforms.PredictCameraFromBoardWithNavigationTranslation(
            new Pose(Vector3.zero, Quaternion.identity),
            Quaternion.identity,
            Quaternion.identity,
            displacement);
        Assert.That(Vector3.Distance(prediction.position, new Vector3(0f, -1f, -1f)), Is.LessThan(0.0001f));
    }

    [Test]
    public void VelocityTranslationShadow_TransformsNavigationDisplacementIntoCurrentCameraFrame()
    {
        var navigationFromCameraT = Quaternion.AngleAxis(90f, Vector3.forward);
        var prediction = ReferenceFrameTransforms.PredictCameraFromBoardWithNavigationTranslation(
            new Pose(Vector3.zero, Quaternion.identity),
            Quaternion.identity,
            navigationFromCameraT,
            new Vector3(1f, 0f, 0f));

        // inverse(Rz(+90)) maps navigation north to camera -Y; the stationary
        // board therefore appears at camera +Y after the camera moves north.
        Assert.That(Vector3.Distance(prediction.position, Vector3.up), Is.LessThan(0.0001f));
        Assert.That(Quaternion.Angle(prediction.rotation, Quaternion.Inverse(navigationFromCameraT)), Is.LessThan(0.001f));
    }

    [Test]
    public void VelocityTranslationShadow_UsesTrapezoidalIntegrationAcrossChangingVelocity()
    {
        var history = new[]
        {
            new DjiCameraPoseProvider.TelemetryVelocitySample(1_000_000_000L, Vector3.zero),
            new DjiCameraPoseProvider.TelemetryVelocitySample(2_000_000_000L, new Vector3(2f, 0f, 0f)),
            new DjiCameraPoseProvider.TelemetryVelocitySample(3_000_000_000L, new Vector3(4f, 0f, 0f))
        };
        Assert.That(ReferenceFrameTransforms.TryIntegrateNavigationVelocityTrapezoidal(
            history,
            1_000_000_000L, Vector3.zero,
            3_000_000_000L, new Vector3(4f, 0f, 0f),
            1_000_000_000L,
            out var displacement,
            out var reason), Is.True);
        Assert.That(reason, Is.Empty);
        Assert.That(displacement.x, Is.EqualTo(4f).Within(0.0001f));
    }

    [Test]
    public void VelocityTranslationShadow_RejectsLargeTelemetryGap()
    {
        Assert.That(ReferenceFrameTransforms.TryIntegrateNavigationVelocityTrapezoidal(
            new[]
            {
                new DjiCameraPoseProvider.TelemetryVelocitySample(1_000_000_000L, Vector3.zero),
                new DjiCameraPoseProvider.TelemetryVelocitySample(3_000_000_000L, Vector3.one)
            },
            1_000_000_000L, Vector3.zero,
            3_000_000_000L, Vector3.one,
            500_000_000L,
            out _,
            out var reason), Is.False);
        Assert.That(reason, Is.EqualTo("HISTORY_GAP_TOO_LARGE"));
    }

    [Test]
    public void VelocityStateHistory_OneStationarySampleHoldsForOnePointFiveSeconds()
    {
        Assert.That(ReferenceFrameTransforms.TryIntegrateNavigationVelocityStateHistory(
            new[]
            {
                new DjiCameraPoseProvider.TelemetryVelocitySample(1_000_000_000L, Vector3.zero)
            },
            1_000_000_000L,
            2_500_000_000L,
            2_000_000_000L,
            out var displacement,
            out var coverage,
            out var reason), Is.True);
        Assert.That(reason, Is.Empty);
        Assert.That(displacement, Is.EqualTo(Vector3.zero));
        Assert.That(coverage.callbackCountInsideInterval, Is.EqualTo(0));
        Assert.That(coverage.TargetSampleAgeMilliseconds(2_500_000_000L), Is.EqualTo(1500d).Within(0.001d));
    }

    [Test]
    public void VelocityStateHistory_OneMovingSampleHoldsToTarget()
    {
        Assert.That(ReferenceFrameTransforms.TryIntegrateNavigationVelocityStateHistory(
            new[]
            {
                new DjiCameraPoseProvider.TelemetryVelocitySample(1_000_000_000L, Vector3.right)
            },
            1_000_000_000L,
            2_000_000_000L,
            2_000_000_000L,
            out var displacement,
            out _,
            out var reason), Is.True);
        Assert.That(reason, Is.Empty);
        Assert.That(Vector3.Distance(displacement, Vector3.right), Is.LessThan(0.0001f));
    }

    [Test]
    public void VelocityStateHistory_TrustedFreshPolicyAcceptsExactlyFiveHundredMilliseconds()
    {
        Assert.That(ReferenceFrameTransforms.TryIntegrateNavigationVelocityStateHistory(
            new[]
            {
                new DjiCameraPoseProvider.TelemetryVelocitySample(1_000_000_000L, Vector3.right)
            },
            1_000_000_000L,
            1_500_000_000L,
            500_000_000L,
            out var displacement,
            out var coverage,
            out var reason), Is.True);
        Assert.That(reason, Is.Empty);
        Assert.That(Vector3.Distance(displacement, Vector3.right * 0.5f), Is.LessThan(0.0001f));
        Assert.That(coverage.TargetSampleAgeMilliseconds(1_500_000_000L), Is.EqualTo(500d).Within(0.001d));
    }

    [Test]
    public void VelocityStateHistory_TrustedFreshPolicyRejectsOlderThanFiveHundredMilliseconds()
    {
        Assert.That(ReferenceFrameTransforms.TryIntegrateNavigationVelocityStateHistory(
            new[]
            {
                new DjiCameraPoseProvider.TelemetryVelocitySample(1_000_000_000L, Vector3.right)
            },
            1_000_000_000L,
            1_501_000_000L,
            500_000_000L,
            out _,
            out _,
            out var reason), Is.False);
        Assert.That(reason, Is.EqualTo("TARGET_STALE"));
    }

    [Test]
    public void VelocityStateHistory_AppliesCallbackChangeWithoutSmearing()
    {
        Assert.That(ReferenceFrameTransforms.TryIntegrateNavigationVelocityStateHistory(
            new[]
            {
                new DjiCameraPoseProvider.TelemetryVelocitySample(1_000_000_000L, Vector3.right),
                new DjiCameraPoseProvider.TelemetryVelocitySample(1_500_000_000L, Vector3.up)
            },
            1_000_000_000L,
            2_000_000_000L,
            2_000_000_000L,
            out var displacement,
            out var coverage,
            out var reason), Is.True);
        Assert.That(reason, Is.Empty);
        Assert.That(Vector3.Distance(displacement, new Vector3(0.5f, 0.5f, 0f)), Is.LessThan(0.0001f));
        Assert.That(coverage.callbackCountInsideInterval, Is.EqualTo(1));
    }

    [Test]
    public void VelocityStateHistory_RejectsStaleBaseline()
    {
        Assert.That(ReferenceFrameTransforms.TryIntegrateNavigationVelocityStateHistory(
            new[]
            {
                new DjiCameraPoseProvider.TelemetryVelocitySample(1_000_000_000L, Vector3.zero)
            },
            4_000_000_000L,
            4_500_000_000L,
            2_000_000_000L,
            out _,
            out _,
            out var reason), Is.False);
        Assert.That(reason, Is.EqualTo("BASELINE_STALE"));
    }

    [Test]
    public void VelocityStateHistory_RejectsStaleTarget()
    {
        Assert.That(ReferenceFrameTransforms.TryIntegrateNavigationVelocityStateHistory(
            new[]
            {
                new DjiCameraPoseProvider.TelemetryVelocitySample(1_000_000_000L, Vector3.zero)
            },
            1_000_000_000L,
            4_000_000_000L,
            2_000_000_000L,
            out _,
            out _,
            out var reason), Is.False);
        Assert.That(reason, Is.EqualTo("TARGET_STALE"));
    }

    [Test]
    public void GuardedHorizontal_PropagationBeforeMinimumAgeRemainsOff()
    {
        var machine = new GuardedHorizontalRenderStateMachine();
        var snapshot = UpdateGuarded(machine, nowMs: 100, gnssMs: 100, propagationAgeMs: 499, fresh: true, north: 0.4f);

        Assert.That(snapshot.state, Is.EqualTo(GuardedHorizontalTranslationState.Off));
        Assert.That(snapshot.active, Is.False);
    }

    [Test]
    public void GuardedHorizontal_OneFreshLargeSampleOnlyArms()
    {
        var machine = new GuardedHorizontalRenderStateMachine();
        var snapshot = UpdateGuarded(machine, nowMs: 600, gnssMs: 600, propagationAgeMs: 600, fresh: true, north: 0.3f);

        Assert.That(snapshot.state, Is.EqualTo(GuardedHorizontalTranslationState.Arming));
        Assert.That(snapshot.active, Is.False);
        Assert.That(snapshot.consistentSampleCount, Is.EqualTo(1));
    }

    [Test]
    public void GuardedHorizontal_TwoConsistentFreshSamplesActivateWithBlend()
    {
        var machine = new GuardedHorizontalRenderStateMachine();
        UpdateGuarded(machine, nowMs: 600, gnssMs: 600, propagationAgeMs: 600, fresh: true, north: 0.3f);
        var activated = UpdateGuarded(machine, nowMs: 700, gnssMs: 700, propagationAgeMs: 700, fresh: true, north: 0.35f);
        var blended = UpdateGuarded(machine, nowMs: 850, gnssMs: 700, propagationAgeMs: 850, fresh: true, north: 0.35f);

        Assert.That(activated.state, Is.EqualTo(GuardedHorizontalTranslationState.Active));
        Assert.That(activated.blendAlpha, Is.EqualTo(0f).Within(0.001f));
        Assert.That(blended.blendAlpha, Is.EqualTo(0.5f).Within(0.001f));
        Assert.That(blended.appliedCorrectionInNavigation.y, Is.EqualTo(0f).Within(0.0001f));
    }

    [Test]
    public void GuardedHorizontal_InconsistentSecondDirectionCannotActivate()
    {
        var machine = new GuardedHorizontalRenderStateMachine();
        UpdateGuarded(machine, nowMs: 600, gnssMs: 600, propagationAgeMs: 600, fresh: true, north: 0.3f);
        var snapshot = UpdateGuarded(machine, nowMs: 700, gnssMs: 700, propagationAgeMs: 700, fresh: true, east: 0.3f);

        Assert.That(snapshot.state, Is.EqualTo(GuardedHorizontalTranslationState.Arming));
        Assert.That(snapshot.active, Is.False);
        Assert.That(snapshot.consistentSampleCount, Is.EqualTo(1));
    }

    [Test]
    public void GuardedHorizontal_StaleGnssCannotArm()
    {
        var machine = new GuardedHorizontalRenderStateMachine();
        var snapshot = UpdateGuarded(machine, nowMs: 600, gnssMs: 200, propagationAgeMs: 600, fresh: false, north: 0.5f);

        Assert.That(snapshot.state, Is.EqualTo(GuardedHorizontalTranslationState.Off));
    }

    [Test]
    public void GuardedHorizontal_ActiveWithinHysteresisBandStaysActive()
    {
        var machine = ActivateGuarded();
        var snapshot = UpdateGuarded(machine, nowMs: 1000, gnssMs: 1000, propagationAgeMs: 1000, fresh: true, north: 0.20f);

        Assert.That(snapshot.state, Is.EqualTo(GuardedHorizontalTranslationState.Active));
    }

    [Test]
    public void GuardedHorizontal_ActiveBelowDisableThresholdDeactivatesSmoothly()
    {
        var machine = ActivateGuarded();
        var deactivating = UpdateGuarded(machine, nowMs: 1000, gnssMs: 1000, propagationAgeMs: 1000, fresh: true, north: 0.15f);
        var finished = UpdateGuarded(machine, nowMs: 1301, gnssMs: 1000, propagationAgeMs: 1301, fresh: true, north: 0.15f);

        Assert.That(deactivating.state, Is.EqualTo(GuardedHorizontalTranslationState.Deactivating));
        Assert.That(deactivating.blendAlpha, Is.EqualTo(1f).Within(0.001f));
        Assert.That(finished.state, Is.EqualTo(GuardedHorizontalTranslationState.Off));
    }

    [Test]
    public void GuardedHorizontal_StaleGnssUsesGraceBeforeDeactivation()
    {
        var machine = ActivateGuarded();
        var grace = UpdateGuarded(machine, nowMs: 1000, gnssMs: 0, propagationAgeMs: 1000, fresh: false);
        var deactivating = UpdateGuarded(machine, nowMs: 1301, gnssMs: 0, propagationAgeMs: 1301, fresh: false);

        Assert.That(grace.state, Is.EqualTo(GuardedHorizontalTranslationState.GnssGrace));
        Assert.That(grace.active, Is.True);
        Assert.That(deactivating.state, Is.EqualTo(GuardedHorizontalTranslationState.Deactivating));
    }

    [Test]
    public void GuardedHorizontal_ResetAndPropagationExpiryClearContribution()
    {
        var machine = ActivateGuarded();
        var expired = UpdateGuarded(machine, nowMs: 2100, gnssMs: 2100, propagationAgeMs: 2001, fresh: true, north: 0.4f);
        var reset = machine.Reset(new GuardedHorizontalObservation(2200_000_000L, 0L, float.PositiveInfinity, false, Vector2.zero, float.PositiveInfinity), "VISUAL_ACCEPTED");

        Assert.That(expired.state, Is.EqualTo(GuardedHorizontalTranslationState.Off));
        Assert.That(reset.active, Is.False);
        Assert.That(reset.appliedCorrectionInNavigation, Is.EqualTo(Vector2.zero));
    }

    [Test]
    public void GuardedHorizontal_AppliesOnlyHorizontalNavigationTranslationAndPreservesRotation()
    {
        var baseline = new Pose(new Vector3(1f, 2f, 3f), Quaternion.Euler(10f, 20f, 30f));
        var navigationFromCamera = Quaternion.Euler(5f, 40f, -10f);
        var rotationOnly = ReferenceFrameTransforms.PredictCameraFromBoardRotationOnly(
            baseline,
            Quaternion.identity,
            navigationFromCamera);
        var guarded = ReferenceFrameTransforms.PredictCameraFromBoardWithNavigationTranslation(
            baseline,
            Quaternion.identity,
            navigationFromCamera,
            new Vector3(0.3f, -0.2f, 0f));

        Assert.That(guarded.rotation, Is.EqualTo(rotationOnly.rotation));
        var appliedInNavigation = navigationFromCamera * (rotationOnly.position - guarded.position);
        Assert.That(appliedInNavigation.x, Is.EqualTo(0.3f).Within(0.0001f));
        Assert.That(appliedInNavigation.y, Is.EqualTo(-0.2f).Within(0.0001f));
        Assert.That(appliedInNavigation.z, Is.EqualTo(0f).Within(0.0001f));
    }

    [Test]
    public void RenderSources_ExposeOneTelemetryAuthority()
    {
        Assert.That(Enum.GetValues(typeof(DjiRenderPoseSource)), Is.EquivalentTo(new[]
        {
            DjiRenderPoseSource.None,
            DjiRenderPoseSource.Visual,
            DjiRenderPoseSource.TelemetryPropagated
        }));
    }

    [TestCase(0f, GuardedHorizontalTranslationState.Off)]
    [TestCase(499f, GuardedHorizontalTranslationState.Off)]
    [TestCase(500f, GuardedHorizontalTranslationState.Arming)]
    [TestCase(501f, GuardedHorizontalTranslationState.Arming)]
    [TestCase(1999f, GuardedHorizontalTranslationState.Arming)]
    [TestCase(2000f, GuardedHorizontalTranslationState.Arming)]
    [TestCase(2001f, GuardedHorizontalTranslationState.Off)]
    public void GuardedHorizontal_PropagationAgeEligibilityHasInclusiveBoundaries(
        float propagationAgeMilliseconds,
        GuardedHorizontalTranslationState expectedState)
    {
        var snapshot = UpdateGuardedWithConfiguration(
            new GuardedHorizontalRenderStateMachine(),
            new GuardedHorizontalRenderConfiguration(),
            nowMs: 1000,
            gnssMs: 1000,
            propagationAgeMs: propagationAgeMilliseconds,
            fresh: true,
            correction: new Vector2(0.30f, 0f));

        Assert.That(snapshot.state, Is.EqualTo(expectedState));
        Assert.That(snapshot.active, Is.False);
    }

    [TestCase(0f, true)]
    [TestCase(299f, true)]
    [TestCase(300f, true)]
    [TestCase(301f, false)]
    public void GuardedHorizontal_GnssFreshnessBoundaryUsesInclusiveMaximumAge(
        float gnssAgeMilliseconds,
        bool expectedFresh)
    {
        var configuration = new GuardedHorizontalRenderConfiguration();
        // The provider establishes freshness before calling the pure state machine:
        // a GNSS sample at exactly maxGnssAgeMilliseconds remains usable.
        var fresh = gnssAgeMilliseconds <= configuration.maxGnssAgeMilliseconds;
        var snapshot = UpdateGuardedWithConfiguration(
            new GuardedHorizontalRenderStateMachine(),
            configuration,
            nowMs: 1000,
            gnssMs: 1000 - (long)gnssAgeMilliseconds,
            propagationAgeMs: 1000f,
            fresh: fresh,
            correction: new Vector2(0.30f, 0f),
            gnssAgeMs: gnssAgeMilliseconds);

        Assert.That(fresh, Is.EqualTo(expectedFresh));
        Assert.That(snapshot.state, Is.EqualTo(expectedFresh
            ? GuardedHorizontalTranslationState.Arming
            : GuardedHorizontalTranslationState.Off));
    }

    [Test]
    public void GuardedHorizontal_FiveHundredMillisecondGnssSampleCannotArm()
    {
        var configuration = new GuardedHorizontalRenderConfiguration();
        Assert.That(configuration.maxGnssAgeMilliseconds, Is.EqualTo(300f));

        var snapshot = UpdateGuardedWithConfiguration(
            new GuardedHorizontalRenderStateMachine(),
            configuration,
            nowMs: 1000,
            gnssMs: 500,
            propagationAgeMs: 1000f,
            fresh: false,
            correction: new Vector2(0.30f, 0f),
            gnssAgeMs: 500f);

        Assert.That(snapshot.state, Is.EqualTo(GuardedHorizontalTranslationState.Off));
        Assert.That(snapshot.active, Is.False);
    }

    [TestCase(0.249f, GuardedHorizontalTranslationState.Off)]
    [TestCase(0.250f, GuardedHorizontalTranslationState.Arming)]
    [TestCase(0.251f, GuardedHorizontalTranslationState.Arming)]
    public void GuardedHorizontal_EnableThresholdHasInclusiveBoundary(
        float correctionMagnitude,
        GuardedHorizontalTranslationState expectedState)
    {
        var snapshot = UpdateGuardedWithConfiguration(
            new GuardedHorizontalRenderStateMachine(),
            new GuardedHorizontalRenderConfiguration(),
            nowMs: 600,
            gnssMs: 600,
            propagationAgeMs: 600f,
            fresh: true,
            correction: new Vector2(correctionMagnitude, 0f));

        Assert.That(snapshot.state, Is.EqualTo(expectedState));
        Assert.That(snapshot.active, Is.False);
    }

    [TestCase(0.149f, GuardedHorizontalTranslationState.Deactivating)]
    [TestCase(0.150f, GuardedHorizontalTranslationState.Deactivating)]
    [TestCase(0.151f, GuardedHorizontalTranslationState.Active)]
    [TestCase(0.200f, GuardedHorizontalTranslationState.Active)]
    [TestCase(0.249f, GuardedHorizontalTranslationState.Active)]
    public void GuardedHorizontal_DisableThresholdPreservesHysteresisBand(
        float correctionMagnitude,
        GuardedHorizontalTranslationState expectedState)
    {
        var machine = ActivateGuarded();
        var snapshot = UpdateGuardedWithConfiguration(
            machine,
            new GuardedHorizontalRenderConfiguration(),
            nowMs: 1100,
            gnssMs: 1100,
            propagationAgeMs: 1100f,
            fresh: true,
            correction: new Vector2(correctionMagnitude, 0f));

        Assert.That(snapshot.state, Is.EqualTo(expectedState));
    }

    [TestCase(0f, true)]
    [TestCase(30f, true)]
    [TestCase(44.9f, true)]
    [TestCase(45f, true)]
    [TestCase(45.1f, false)]
    [TestCase(60f, false)]
    [TestCase(90f, false)]
    [TestCase(180f, false)]
    public void GuardedHorizontal_DirectionConsistencyUsesInclusiveFortyFiveDegreeTolerance(
        float differenceDegrees,
        bool expectedActive)
    {
        var machine = new GuardedHorizontalRenderStateMachine();
        var configuration = new GuardedHorizontalRenderConfiguration();
        UpdateGuardedWithConfiguration(machine, configuration, 600, 600, 600f, true, new Vector2(0.30f, 0f));
        var radians = differenceDegrees * Mathf.Deg2Rad;
        var candidate = new Vector2(Mathf.Cos(radians), Mathf.Sin(radians)) * 0.31f;
        var snapshot = UpdateGuardedWithConfiguration(machine, configuration, 700, 700, 700f, true, candidate);

        Assert.That(snapshot.state == GuardedHorizontalTranslationState.Active, Is.EqualTo(expectedActive));
        Assert.That(float.IsNaN(snapshot.directionDifferenceDegrees), Is.False);
    }

    [TestCase(0f)]
    [TestCase(0.001f)]
    [TestCase(0.01f)]
    public void GuardedHorizontal_TinyCorrectionsNeverActivateOrProduceInvalidDirection(float magnitude)
    {
        var machine = new GuardedHorizontalRenderStateMachine();
        var snapshot = UpdateGuardedWithConfiguration(
            machine, new GuardedHorizontalRenderConfiguration(), 600, 600, 600f, true, new Vector2(magnitude, -magnitude));

        Assert.That(snapshot.state, Is.EqualTo(GuardedHorizontalTranslationState.Off));
        Assert.That(snapshot.active, Is.False);
        Assert.That(float.IsInfinity(snapshot.directionDifferenceDegrees) || float.IsNaN(snapshot.directionDifferenceDegrees), Is.True);
        Assert.That(float.IsNaN(snapshot.blendAlpha) || float.IsInfinity(snapshot.blendAlpha), Is.False);
    }

    [TestCase(0L, 0f)]
    [TestCase(75L, 0.25f)]
    [TestCase(150L, 0.5f)]
    [TestCase(225L, 0.75f)]
    [TestCase(300L, 1f)]
    [TestCase(450L, 1f)]
    public void GuardedHorizontal_ActivationBlendIsTimeBasedAndClamped(long elapsedMilliseconds, float expectedAlpha)
    {
        var machine = new GuardedHorizontalRenderStateMachine();
        var configuration = new GuardedHorizontalRenderConfiguration();
        UpdateGuardedWithConfiguration(machine, configuration, 600, 600, 600f, true, new Vector2(0.30f, 0f));
        UpdateGuardedWithConfiguration(machine, configuration, 700, 700, 700f, true, new Vector2(0.31f, 0f));
        var snapshot = UpdateGuardedWithConfiguration(machine, configuration, 700 + elapsedMilliseconds, 700, 700f + elapsedMilliseconds, true, new Vector2(0.31f, 0f));

        Assert.That(snapshot.state, Is.EqualTo(GuardedHorizontalTranslationState.Active));
        Assert.That(snapshot.blendAlpha, Is.EqualTo(expectedAlpha).Within(0.001f));
        Assert.That(snapshot.appliedCorrectionInNavigation.y, Is.EqualTo(0f).Within(0.00001f));
    }

    [TestCase(0L, GuardedHorizontalTranslationState.GnssGrace)]
    [TestCase(299L, GuardedHorizontalTranslationState.GnssGrace)]
    [TestCase(300L, GuardedHorizontalTranslationState.GnssGrace)]
    [TestCase(301L, GuardedHorizontalTranslationState.Deactivating)]
    public void GuardedHorizontal_GnssGraceExpiresOnlyAfterConfiguredDuration(
        long graceElapsedMilliseconds,
        GuardedHorizontalTranslationState expectedState)
    {
        var machine = ActivateGuarded();
        UpdateGuardedWithConfiguration(machine, new GuardedHorizontalRenderConfiguration(), 1000, 0, 1000f, false, Vector2.zero);
        var snapshot = UpdateGuardedWithConfiguration(machine, new GuardedHorizontalRenderConfiguration(), 1000 + graceElapsedMilliseconds, 0, 1000f + graceElapsedMilliseconds, false, Vector2.zero);

        Assert.That(snapshot.state, Is.EqualTo(expectedState));
        if (expectedState == GuardedHorizontalTranslationState.GnssGrace)
            Assert.That(snapshot.appliedCorrectionInNavigation.magnitude, Is.GreaterThan(0f));
    }

    [Test]
    public void GuardedHorizontal_FreshRecoveryDuringGraceReturnsActiveWithoutRearming()
    {
        var machine = ActivateGuarded();
        UpdateGuardedWithConfiguration(machine, new GuardedHorizontalRenderConfiguration(), 1000, 0, 1000f, false, Vector2.zero);
        var recovered = UpdateGuardedWithConfiguration(machine, new GuardedHorizontalRenderConfiguration(), 1100, 1100, 1100f, true, new Vector2(0.31f, 0f));

        Assert.That(recovered.state, Is.EqualTo(GuardedHorizontalTranslationState.Active));
        Assert.That(recovered.consistentSampleCount, Is.EqualTo(2));
        Assert.That(recovered.active, Is.True);
    }

    [TestCase(0L, 1f)]
    [TestCase(75L, 0.75f)]
    [TestCase(150L, 0.5f)]
    [TestCase(225L, 0.25f)]
    [TestCase(300L, 0f)]
    public void GuardedHorizontal_DeactivationBlendFadesToZeroWithoutResidual(
        long elapsedMilliseconds,
        float expectedAlpha)
    {
        var machine = ActivateGuarded();
        UpdateGuardedWithConfiguration(machine, new GuardedHorizontalRenderConfiguration(), 1000, 1000, 1000f, true, new Vector2(0.15f, 0f));
        var snapshot = UpdateGuardedWithConfiguration(machine, new GuardedHorizontalRenderConfiguration(), 1000 + elapsedMilliseconds, 1000, 1000f + elapsedMilliseconds, true, new Vector2(0.15f, 0f));

        Assert.That(snapshot.blendAlpha, Is.EqualTo(expectedAlpha).Within(0.001f));
        if (elapsedMilliseconds >= 300L)
        {
            Assert.That(snapshot.state, Is.EqualTo(GuardedHorizontalTranslationState.Off));
            Assert.That(snapshot.appliedCorrectionInNavigation, Is.EqualTo(Vector2.zero));
        }
    }

    [Test]
    public void GuardedHorizontal_ReactivationDuringDeactivationStartsANewConsistencySequence()
    {
        var machine = ActivateGuarded();
        UpdateGuardedWithConfiguration(machine, new GuardedHorizontalRenderConfiguration(), 1000, 1000, 1000f, true, new Vector2(0.15f, 0f));
        var arming = UpdateGuardedWithConfiguration(machine, new GuardedHorizontalRenderConfiguration(), 1050, 1050, 1050f, true, new Vector2(0.30f, 0f));
        var active = UpdateGuardedWithConfiguration(machine, new GuardedHorizontalRenderConfiguration(), 1100, 1100, 1100f, true, new Vector2(0.31f, 0f));

        Assert.That(arming.state, Is.EqualTo(GuardedHorizontalTranslationState.Arming));
        Assert.That(arming.consistentSampleCount, Is.EqualTo(1));
        Assert.That(arming.blendAlpha, Is.EqualTo(0f));
        Assert.That(active.state, Is.EqualTo(GuardedHorizontalTranslationState.Active));
    }

    [TestCase("VISUAL_ACCEPTED")]
    [TestCase("PROVIDER_DISABLED")]
    [TestCase("PROPAGATION_STOPPED")]
    public void GuardedHorizontal_ResetClearsStateForEveryLifecycleBoundary(string reason)
    {
        var machine = ActivateGuarded();
        var reset = machine.Reset(
            new GuardedHorizontalObservation(2000_000_000L, 0L, 1000f, false, Vector2.zero, float.PositiveInfinity), reason);
        var next = UpdateGuardedWithConfiguration(machine, new GuardedHorizontalRenderConfiguration(), 2100, 2100, 1000f, true, new Vector2(0.30f, 0f));

        Assert.That(reset.state, Is.EqualTo(GuardedHorizontalTranslationState.Off));
        Assert.That(reset.appliedCorrectionInNavigation, Is.EqualTo(Vector2.zero));
        Assert.That(reset.consistentSampleCount, Is.Zero);
        Assert.That(next.state, Is.EqualTo(GuardedHorizontalTranslationState.Arming));
        Assert.That(next.consistentSampleCount, Is.EqualTo(1));
    }

    [Test]
    public void GuardedHorizontal_DuplicateGnssCallbackCannotCountTwice()
    {
        var machine = new GuardedHorizontalRenderStateMachine();
        var configuration = new GuardedHorizontalRenderConfiguration();
        UpdateGuardedWithConfiguration(machine, configuration, 600, 600, 600f, true, new Vector2(0.30f, 0f));
        var duplicate = UpdateGuardedWithConfiguration(machine, configuration, 650, 600, 650f, true, new Vector2(0.30f, 0f));
        var distinct = UpdateGuardedWithConfiguration(machine, configuration, 700, 700, 700f, true, new Vector2(0.30f, 0f));

        Assert.That(duplicate.state, Is.EqualTo(GuardedHorizontalTranslationState.Arming));
        Assert.That(duplicate.consistentSampleCount, Is.EqualTo(1));
        Assert.That(distinct.state, Is.EqualTo(GuardedHorizontalTranslationState.Active));
    }

    [Test]
    public void GuardedHorizontal_PollingAgedSampleDoesNotBreakArmingSequence()
    {
        var machine = new GuardedHorizontalRenderStateMachine();
        var configuration = new GuardedHorizontalRenderConfiguration();
        UpdateGuardedWithConfiguration(machine, configuration, 600, 600, 600f, true, new Vector2(0.30f, 0f));
        var stale = UpdateGuardedWithConfiguration(machine, configuration, 950, 600, 950f, false, new Vector2(0.30f, 0f));
        var restart = UpdateGuardedWithConfiguration(machine, configuration, 1055, 1055, 1055f, true, new Vector2(0.30f, 0f));
        var belowThreshold = UpdateGuardedWithConfiguration(machine, configuration, 1150, 1150, 1150f, true, new Vector2(0.24f, 0f));

        Assert.That(stale.state, Is.EqualTo(GuardedHorizontalTranslationState.Arming));
        Assert.That(stale.consistentSampleCount, Is.EqualTo(1));
        Assert.That(restart.state, Is.EqualTo(GuardedHorizontalTranslationState.Active));
        Assert.That(belowThreshold.state, Is.EqualTo(GuardedHorizontalTranslationState.Active));
        Assert.That(belowThreshold.consistentSampleCount, Is.EqualTo(2));
    }

    [TestCase(220L)]
    [TestCase(235L)]
    public void GuardedHorizontal_DistinctNormalCadenceCallbacksActivateAfterPollingFrames(long callbackIntervalMs)
    {
        var machine = new GuardedHorizontalRenderStateMachine();
        var configuration = new GuardedHorizontalRenderConfiguration();
        var first = UpdateGuardedWithConfiguration(
            machine, configuration, 600, 600, 600f, true, new Vector2(0.30f, 0f));
        var poll = UpdateGuardedWithConfiguration(
            machine, configuration, 700, 600, 700f, false, new Vector2(0.30f, 0f));
        var active = UpdateGuardedWithConfiguration(
            machine, configuration, 600 + callbackIntervalMs, 600 + callbackIntervalMs,
            600f + callbackIntervalMs, true, new Vector2(0.31f, 0f));

        Assert.That(first.state, Is.EqualTo(GuardedHorizontalTranslationState.Arming));
        Assert.That(poll.state, Is.EqualTo(GuardedHorizontalTranslationState.Arming));
        Assert.That(poll.consistentSampleCount, Is.EqualTo(1));
        Assert.That(active.state, Is.EqualTo(GuardedHorizontalTranslationState.Active));
        Assert.That(active.consistentSampleCount, Is.EqualTo(2));
    }

    [Test]
    public void GuardedHorizontal_ArmingTimesOutWithoutSecondDistinctCallback()
    {
        var machine = new GuardedHorizontalRenderStateMachine();
        var configuration = new GuardedHorizontalRenderConfiguration();
        UpdateGuardedWithConfiguration(machine, configuration, 600, 600, 600f, true, new Vector2(0.30f, 0f));
        var timeout = UpdateGuardedWithConfiguration(
            machine, configuration, 1201, 600, 1201f, false, new Vector2(0.30f, 0f));

        Assert.That(timeout.state, Is.EqualTo(GuardedHorizontalTranslationState.Off));
        Assert.That(timeout.reason, Is.EqualTo("ARMING_TIMEOUT"));
        Assert.That(timeout.consistentSampleCount, Is.Zero);
        Assert.That(timeout.armingAgeMilliseconds, Is.GreaterThan(configuration.armingTimeoutMilliseconds));
        Assert.That(timeout.firstQualifyingSampleTimestampNs, Is.EqualTo(600_000_000L));
    }

    [Test]
    public void GuardedHorizontal_StaleDistinctSecondCallbackCannotActivate()
    {
        var machine = new GuardedHorizontalRenderStateMachine();
        var configuration = new GuardedHorizontalRenderConfiguration();
        UpdateGuardedWithConfiguration(machine, configuration, 600, 600, 600f, true, new Vector2(0.30f, 0f));
        var stale = UpdateGuardedWithConfiguration(
            machine, configuration, 820, 820, 820f, false, new Vector2(0.31f, 0f));

        Assert.That(stale.state, Is.EqualTo(GuardedHorizontalTranslationState.Off));
        Assert.That(stale.reason, Is.EqualTo("STALE_GNSS_CALLBACK"));
        Assert.That(stale.active, Is.False);
    }

    [Test]
    public void GuardedHorizontal_OutOfOrderCallbackDoesNotCancelArming()
    {
        var machine = new GuardedHorizontalRenderStateMachine();
        var configuration = new GuardedHorizontalRenderConfiguration();
        UpdateGuardedWithConfiguration(machine, configuration, 600, 600, 600f, true, new Vector2(0.30f, 0f));
        var outOfOrder = UpdateGuardedWithConfiguration(
            machine, configuration, 700, 550, 700f, true, new Vector2(0.31f, 0f));

        Assert.That(outOfOrder.state, Is.EqualTo(GuardedHorizontalTranslationState.Arming));
        Assert.That(outOfOrder.consistentSampleCount, Is.EqualTo(1));
    }

    [Test]
    public void GuardedHorizontal_ModeToggleResetsArmingAndOldCallbackCannotActivate()
    {
        var machine = new GuardedHorizontalRenderStateMachine();
        var configuration = new GuardedHorizontalRenderConfiguration();
        UpdateGuardedWithConfiguration(machine, configuration, 600, 600, 600f, true, new Vector2(0.30f, 0f));
        var shadow = UpdateGuardedWithConfiguration(
            machine, configuration, 650, 600, 650f, false, Vector2.zero,
            mode: HorizontalTranslationRenderMode.ShadowOnly);
        var oldCallback = UpdateGuardedWithConfiguration(
            machine, configuration, 700, 600, 700f, true, new Vector2(0.31f, 0f));

        Assert.That(shadow.state, Is.EqualTo(GuardedHorizontalTranslationState.Off));
        Assert.That(oldCallback.state, Is.EqualTo(GuardedHorizontalTranslationState.Off));
    }

    [Test]
    public void GuardedHorizontal_OutOfOrderGnssCannotDeactivateAnActiveContribution()
    {
        var machine = ActivateGuarded();
        var stale = UpdateGuardedWithConfiguration(
            machine,
            new GuardedHorizontalRenderConfiguration(),
            nowMs: 1100,
            gnssMs: 650,
            propagationAgeMs: 1100f,
            fresh: true,
            correction: new Vector2(0.10f, 0f));

        Assert.That(stale.state, Is.EqualTo(GuardedHorizontalTranslationState.Active));
        Assert.That(stale.appliedCorrectionInNavigation.x, Is.EqualTo(0.30f).Within(0.0001f));
    }

    [TestCase(GuardedHorizontalTranslationState.Arming)]
    [TestCase(GuardedHorizontalTranslationState.Active)]
    [TestCase(GuardedHorizontalTranslationState.GnssGrace)]
    [TestCase(GuardedHorizontalTranslationState.Deactivating)]
    public void GuardedHorizontal_PropagationExpiryResetsEveryNonOffState(
        GuardedHorizontalTranslationState initialState)
    {
        var machine = CreateGuardedMachineInState(initialState);
        var expired = UpdateGuardedWithConfiguration(
            machine,
            new GuardedHorizontalRenderConfiguration(),
            2200,
            2200,
            2001f,
            true,
            new Vector2(0.30f, 0f));

        Assert.That(expired.state, Is.EqualTo(GuardedHorizontalTranslationState.Off));
        Assert.That(expired.active, Is.False);
        Assert.That(expired.appliedCorrectionInNavigation, Is.EqualTo(Vector2.zero));
        Assert.That(expired.reason, Is.EqualTo("PROPAGATION_AGE_EXPIRED"));
    }

    [Test]
    public void GuardedHorizontal_ShadowOnlyNeverActivatesEvenForQualifyingSamples()
    {
        var machine = new GuardedHorizontalRenderStateMachine();
        var configuration = new GuardedHorizontalRenderConfiguration();
        var first = UpdateGuardedWithConfiguration(machine, configuration, 600, 600, 600f, true, new Vector2(0.30f, 0f), mode: HorizontalTranslationRenderMode.ShadowOnly);
        var second = UpdateGuardedWithConfiguration(machine, configuration, 700, 700, 700f, true, new Vector2(0.31f, 0f), mode: HorizontalTranslationRenderMode.ShadowOnly);

        Assert.That(first.state, Is.EqualTo(GuardedHorizontalTranslationState.Off));
        Assert.That(second.state, Is.EqualTo(GuardedHorizontalTranslationState.Off));
        Assert.That(second.active, Is.False);
        Assert.That(second.reason, Is.EqualTo("SHADOW_ONLY"));
    }

    [Test]
    public void GuardedHorizontal_TranslationPreservesVerticalAndRotationForAllHorizontalQuadrants()
    {
        var baseline = new Pose(new Vector3(1f, 2f, 3f), Quaternion.Euler(10f, 20f, 30f));
        var navigationFromCamera = Quaternion.Euler(-5f, 35f, 12f);
        var rotationOnly = ReferenceFrameTransforms.PredictCameraFromBoardRotationOnly(baseline, Quaternion.identity, navigationFromCamera);
        foreach (var correction in new[]
                 {
                     new Vector2(0.30f, 0f), new Vector2(0f, 0.30f),
                     new Vector2(0.20f, 0.20f), new Vector2(-0.20f, -0.20f)
                 })
        {
            var guarded = ReferenceFrameTransforms.PredictCameraFromBoardWithNavigationTranslation(
                baseline, Quaternion.identity, navigationFromCamera, new Vector3(correction.x, correction.y, 0f));
            var appliedInNavigation = navigationFromCamera * (rotationOnly.position - guarded.position);

            Assert.That(Quaternion.Angle(guarded.rotation, rotationOnly.rotation), Is.LessThan(0.0001f));
            Assert.That(appliedInNavigation.x, Is.EqualTo(correction.x).Within(0.0001f));
            Assert.That(appliedInNavigation.y, Is.EqualTo(correction.y).Within(0.0001f));
            Assert.That(appliedInNavigation.z, Is.EqualTo(0f).Within(0.0001f));
        }
    }

    [Test]
    public void GuardedHorizontal_DeterministicFuzzMaintainsStateAndNumericInvariants()
    {
        var random = new System.Random(48151623);
        var machine = new GuardedHorizontalRenderStateMachine();
        var configuration = new GuardedHorizontalRenderConfiguration();
        long nowMs = 0;
        long gnssMs = 0;
        for (var i = 0; i < 512; i++)
        {
            nowMs += random.Next(1, 80);
            if (random.NextDouble() > 0.35d)
                gnssMs = nowMs - random.Next(0, 450);
            var propagationAgeMs = random.Next(0, 2300);
            var fresh = random.NextDouble() > 0.25d;
            var correction = new Vector2(
                (float)(random.NextDouble() * 2d - 1d) * 1.5f,
                (float)(random.NextDouble() * 2d - 1d) * 1.5f);
            var snapshot = UpdateGuardedWithConfiguration(
                machine, configuration, nowMs, gnssMs, propagationAgeMs, fresh, correction,
                fresh ? 0f : float.PositiveInfinity);

            Assert.That(Enum.IsDefined(typeof(GuardedHorizontalTranslationState), snapshot.state), Is.True);
            Assert.That(snapshot.blendAlpha, Is.InRange(0f, 1f));
            Assert.That(float.IsNaN(snapshot.appliedCorrectionInNavigation.x) || float.IsInfinity(snapshot.appliedCorrectionInNavigation.x), Is.False);
            Assert.That(float.IsNaN(snapshot.appliedCorrectionInNavigation.y) || float.IsInfinity(snapshot.appliedCorrectionInNavigation.y), Is.False);
            if (propagationAgeMs < configuration.minPropagationAgeMilliseconds ||
                propagationAgeMs > configuration.maxPropagationAgeMilliseconds)
            {
                Assert.That(snapshot.state, Is.EqualTo(GuardedHorizontalTranslationState.Off));
                Assert.That(snapshot.active, Is.False);
            }
        }
    }

    private static GuardedHorizontalRenderStateMachine ActivateGuarded()
    {
        var machine = new GuardedHorizontalRenderStateMachine();
        UpdateGuarded(machine, nowMs: 600, gnssMs: 600, propagationAgeMs: 600, fresh: true, north: 0.3f);
        UpdateGuarded(machine, nowMs: 700, gnssMs: 700, propagationAgeMs: 700, fresh: true, north: 0.3f);
        UpdateGuarded(machine, nowMs: 1000, gnssMs: 700, propagationAgeMs: 1000, fresh: true, north: 0.3f);
        return machine;
    }

    private static GuardedHorizontalRenderStateMachine CreateGuardedMachineInState(
        GuardedHorizontalTranslationState state)
    {
        var machine = new GuardedHorizontalRenderStateMachine();
        var configuration = new GuardedHorizontalRenderConfiguration();
        UpdateGuardedWithConfiguration(machine, configuration, 600, 600, 600f, true, new Vector2(0.30f, 0f));
        if (state == GuardedHorizontalTranslationState.Arming)
            return machine;

        UpdateGuardedWithConfiguration(machine, configuration, 700, 700, 700f, true, new Vector2(0.30f, 0f));
        if (state == GuardedHorizontalTranslationState.Active)
            return machine;

        if (state == GuardedHorizontalTranslationState.GnssGrace)
        {
            UpdateGuardedWithConfiguration(machine, configuration, 1000, 0, 1000f, false, Vector2.zero);
            return machine;
        }

        UpdateGuardedWithConfiguration(machine, configuration, 1000, 1000, 1000f, true, new Vector2(0.15f, 0f));
        return machine;
    }

    private static GuardedHorizontalStateSnapshot UpdateGuarded(
        GuardedHorizontalRenderStateMachine machine,
        long nowMs,
        long gnssMs,
        float propagationAgeMs,
        bool fresh,
        float north = 0f,
        float east = 0f) =>
        machine.Update(
            HorizontalTranslationRenderMode.GuardedGnssRendered,
            new GuardedHorizontalRenderConfiguration(),
            new GuardedHorizontalObservation(
                nowMs * 1_000_000L,
                gnssMs * 1_000_000L,
                propagationAgeMs,
                fresh,
                new Vector2(north, east),
                fresh ? 0f : float.PositiveInfinity));

    private static GuardedHorizontalStateSnapshot UpdateGuardedWithConfiguration(
        GuardedHorizontalRenderStateMachine machine,
        GuardedHorizontalRenderConfiguration configuration,
        long nowMs,
        long gnssMs,
        float propagationAgeMs,
        bool fresh,
        Vector2 correction,
        float gnssAgeMs = 0f,
        HorizontalTranslationRenderMode mode = HorizontalTranslationRenderMode.GuardedGnssRendered) =>
        machine.Update(
            mode,
            configuration,
            new GuardedHorizontalObservation(
                nowMs * 1_000_000L,
                gnssMs * 1_000_000L,
                propagationAgeMs,
                fresh,
                correction,
                fresh ? gnssAgeMs : float.PositiveInfinity));

    private static string EvaluateVisualQuality(
        int markerCount,
        float rmsPixels,
        float maxCornerResidualPixels = 3f,
        float borderDistancePixels = 128f,
        float translationJumpMeters = 0f,
        float observedMarkerSizePixels = 100f)
    {
        if (markerCount == 4)
        {
            var scaleAwareReason = DjiVisualPoseQualityPolicy.EvaluateFourMarkerScaleAware(
                rmsPixels,
                maxCornerResidualPixels,
                observedMarkerSizePixels,
                FourMarkerMaximumNormalizedRms,
                FourMarkerMaximumNormalizedCornerResidual,
                FourMarkerHardMaximumRmsPixels,
                FourMarkerHardMaximumCornerResidualPixels);
            if (!string.IsNullOrEmpty(scaleAwareReason))
                return scaleAwareReason;
        }

        return DjiVisualPoseQualityPolicy.Evaluate(
            markerCount: markerCount,
            reprojectionRmsPixels: rmsPixels,
            maxCornerResidualPixels: maxCornerResidualPixels,
            minimumBorderDistancePixels: borderDistancePixels,
            translationJumpMeters: translationJumpMeters,
            rotationJumpDegrees: 0f,
            allowSingleMarkerHighConfidence: true,
            defaultMaximumReprojectionRmsPixels: DefaultHighConfidenceRmsPixels,
            fourMarkerMaximumReprojectionRmsPixels: float.PositiveInfinity,
            maximumCornerResidualPixels: DefaultHighConfidenceCornerResidualPixels,
            fourMarkerMaximumCornerResidualPixels: float.PositiveInfinity,
            minimumBorderDistancePixelsRequired: 64f,
            maximumTranslationJumpMeters: 0.035f,
            maximumRotationJumpDegrees: 10f,
            evaluateFourMarkerPixelResiduals: false);
    }

    private static bool ReacquisitionReady(
        bool hasHighConfidenceVisualPose,
        long elapsedMilliseconds,
        float positionDifferenceMeters,
        float rotationDifferenceDegrees)
    {
        const long startTimestampNs = 1_000_000_000L;
        var propagated = new Pose(Vector3.zero, Quaternion.identity);
        var visual = new Pose(
            new Vector3(positionDifferenceMeters, 0f, 0f),
            Quaternion.AngleAxis(rotationDifferenceDegrees, Vector3.up));
        return RotationPropagationRenderPolicy.IsVisualReacquisitionReady(
            hasHighConfidenceVisualPose,
            startTimestampNs,
            startTimestampNs + elapsedMilliseconds * 1_000_000L,
            0.25f,
            propagated,
            visual,
            1f,
            6f,
            out _,
            out _);
    }

    private static bool FreshRelocalizationReady(
        bool hasHighConfidenceVisualPose,
        long elapsedMilliseconds)
    {
        const long startTimestampNs = 1_000_000_000L;
        return RotationPropagationRenderPolicy.IsFreshVisualRelocalizationReady(
            hasHighConfidenceVisualPose,
            startTimestampNs,
            startTimestampNs + elapsedMilliseconds * 1_000_000L,
            0.25f);
    }

    private static DjiOutageVisualGroundTruthTracker.TrustedVisualPose TrustedVisualPose(
        Vector3 cameraFromBoardPosition,
        long timestampNs) =>
        new DjiOutageVisualGroundTruthTracker.TrustedVisualPose(
            new Pose(cameraFromBoardPosition, Quaternion.identity),
            timestampNs);

    private static void PrimeExpiredLocalizationState(DjiBoardVisionProvider provider)
    {
        SetPrivateField(provider, "_hasShadowRotationPropagationBaseline", true);
        SetPrivateField(provider, "_shadowPropagationActive", true);
        SetPrivateField(provider, "_hasLatestShadowPrediction", true);
        SetPrivateField(provider, "_propagationStartTimestampNs", 1_000_000_000L);
        SetPrivateField(provider, "_translationShadowStartTimestampNs", 1_000_000_000L);
        SetPrivateField(provider, "_hasPendingReacquisition", true);
        SetPrivateField(provider, "_pendingReacquisitionStartTimestampNs", 1_000_000_000L);
        SetPrivateField(provider, "_hasLastVisualQualityPose", true);
        SetPrivateField(provider, "_lastVisualQualityPoseTimestampNs", 1_000_000_000L);
        SetPrivateField(provider, "_hasLastVisualQualityTelemetryRotation", true);
        SetPrivateField(provider, "_visualReacquisitionStartTimestampNs", 1_000_000_000L);
        SetPrivateField(provider, "_hasVisualReacquisitionCandidate", true);
        SetPrivateField(provider, "_consecutiveTranslationJumpSuspects", 2);
        SetPrivateField(provider, "_consecutiveBorderlineFourMarkerQualityFailures", 2);
        SetPrivateProperty(provider, "HasHighConfidenceCameraFromBoard", true);
        SetPrivateProperty(
            provider,
            "CurrentVisualPoseQuality",
            DjiBoardVisionProvider.VisualPoseQuality.HighConfidence);

        var guardedMachine = GetPrivateField<GuardedHorizontalRenderStateMachine>(
            provider,
            "_guardedHorizontalRenderStateMachine");
        var guardedConfiguration = GetPrivateField<GuardedHorizontalRenderConfiguration>(
            provider,
            "guardedHorizontalRenderConfiguration");
        guardedMachine.Update(
            HorizontalTranslationRenderMode.GuardedGnssRendered,
            guardedConfiguration,
            new GuardedHorizontalObservation(
                1_000_000_000L,
                1_000_000_000L,
                600f,
                true,
                new Vector2(0.5f, 0f),
                0f));
        var active = guardedMachine.Update(
            HorizontalTranslationRenderMode.GuardedGnssRendered,
            guardedConfiguration,
            new GuardedHorizontalObservation(
                1_200_000_000L,
                1_200_000_000L,
                800f,
                true,
                new Vector2(0.5f, 0f),
                0f));
        Assert.That(active.state, Is.EqualTo(GuardedHorizontalTranslationState.Active));
    }

    private static void InvokePrivate(object target, string methodName)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, $"Missing private method {methodName}");
        method.Invoke(target, null);
    }

    private static T GetPrivateField<T>(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing private field {fieldName}");
        return (T)field.GetValue(target);
    }

    private static void SetPrivateField<T>(object target, string fieldName, T value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing private field {fieldName}");
        field.SetValue(target, value);
    }

    private static void SetPrivateProperty<T>(object target, string propertyName, T value)
    {
        var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.That(property, Is.Not.Null, $"Missing property {propertyName}");
        property.SetValue(target, value);
    }
}
