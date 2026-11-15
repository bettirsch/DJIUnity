using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Named rigid-transform helpers. Every matrix is named "parentFromChild" and maps child coordinates into parent coordinates.
/// </summary>
public static class ReferenceFrameTransforms
{
    private const float MatrixTolerance = 0.0001f;

    public enum HybridHorizontalShadowMode
    {
        GnssPlusVelocity,
        GnssOnly,
        VelocityOnly,
        RotationOnlyFallback
    }

    /// <summary>
    /// Chooses a diagnostic-only horizontal fusion mode. The caller remains
    /// responsible for preserving the existing rendered rotation-only path.
    /// </summary>
    public static HybridHorizontalShadowMode ResolveHybridHorizontalShadowMode(
        bool hasGnssDisplacement,
        bool hasFreshVelocityAfterGnss,
        bool hasFreshVelocityFromBaseline) =>
        hasGnssDisplacement && hasFreshVelocityAfterGnss
            ? HybridHorizontalShadowMode.GnssPlusVelocity
            : hasGnssDisplacement
                ? HybridHorizontalShadowMode.GnssOnly
                : hasFreshVelocityFromBaseline
                    ? HybridHorizontalShadowMode.VelocityOnly
                    : HybridHorizontalShadowMode.RotationOnlyFallback;

    /// <summary>
    /// GNSS already covers baseline through its own sample timestamp, so only
    /// the later velocity increment may be added. Down remains unresolved.
    /// </summary>
    public static Vector3 CombineHorizontalGnssAndVelocityIncrement(
        Vector3 gnssDisplacementInNavigation,
        Vector3 velocityIncrementInNavigation) =>
        new(
            gnssDisplacementInNavigation.x + velocityIncrementInNavigation.x,
            gnssDisplacementInNavigation.y + velocityIncrementInNavigation.y,
            0f);

    /// <summary>
    /// Coverage metadata for a zero-order-hold velocity integration. Ages are
    /// always measured in the shared Android monotonic timestamp domain.
    /// </summary>
    public readonly struct VelocityStateHistoryCoverage
    {
        public readonly long baselineSampleTimestampNs;
        public readonly long targetSampleTimestampNs;
        public readonly int callbackCountInsideInterval;

        public VelocityStateHistoryCoverage(
            long baselineSampleTimestampNs,
            long targetSampleTimestampNs,
            int callbackCountInsideInterval)
        {
            this.baselineSampleTimestampNs = baselineSampleTimestampNs;
            this.targetSampleTimestampNs = targetSampleTimestampNs;
            this.callbackCountInsideInterval = callbackCountInsideInterval;
        }

        public double BaselineSampleAgeMilliseconds(long baselineTimestampNs) =>
            baselineSampleTimestampNs > 0L
                ? Math.Max(0d, (baselineTimestampNs - baselineSampleTimestampNs) / 1_000_000d)
                : double.NaN;

        public double TargetSampleAgeMilliseconds(long targetTimestampNs) =>
            targetSampleTimestampNs > 0L
                ? Math.Max(0d, (targetTimestampNs - targetSampleTimestampNs) / 1_000_000d)
                : double.NaN;
    }

    public static Matrix4x4 MatrixFromPose(Pose parentFromChild) =>
        Matrix4x4.TRS(parentFromChild.position, parentFromChild.rotation, Vector3.one);

    public static Pose PoseFromMatrix(Matrix4x4 parentFromChild)
    {
        var translation = parentFromChild.GetColumn(3);
        return new Pose(new Vector3(translation.x, translation.y, translation.z), parentFromChild.rotation);
    }

    public static Pose Invert(Pose parentFromChild) => PoseFromMatrix(MatrixFromPose(parentFromChild).inverse);

    public static Matrix4x4 Compose(Matrix4x4 parentFromChild, Matrix4x4 childFromGrandchild) =>
        parentFromChild * childFromGrandchild;

    public static Matrix4x4 WorldFromBoard(Pose worldFromBoard) => MatrixFromPose(worldFromBoard);
    public static Matrix4x4 BoardFromWorld(Pose worldFromBoard) => WorldFromBoard(worldFromBoard).inverse;
    public static Matrix4x4 WorldFromCamera(Pose worldFromCamera) => MatrixFromPose(worldFromCamera);
    public static Matrix4x4 CameraFromWorld(Pose worldFromCamera) => WorldFromCamera(worldFromCamera).inverse;

    public static Matrix4x4 CameraFromBoard(Matrix4x4 cameraFromWorld, Matrix4x4 worldFromBoard) =>
        Compose(cameraFromWorld, worldFromBoard);

    public static Matrix4x4 BoardFromCamera(Matrix4x4 boardFromWorld, Matrix4x4 worldFromCamera) =>
        Compose(boardFromWorld, worldFromCamera);

    /// <summary>
    /// Computes T_cameraT_camera0 from two telemetry T_navigation_camera
    /// rotations. The result maps vectors expressed in the baseline camera
    /// frame into the current camera frame.
    /// </summary>
    public static Quaternion CameraTFromCamera0Rotation(
        Quaternion navigationFromCamera0,
        Quaternion navigationFromCameraT) =>
        Quaternion.Normalize(
            Quaternion.Inverse(navigationFromCameraT) * navigationFromCamera0);

    /// <summary>
    /// Predicts T_cameraT_board from a visual baseline using telemetry
    /// rotation only. Translation is deliberately a zero-camera-translation
    /// approximation: both the board orientation and its camera-space
    /// translation are rotated by T_cameraT_camera0.
    /// </summary>
    public static Pose PredictCameraFromBoardRotationOnly(
        Pose camera0FromBoard,
        Quaternion navigationFromCamera0,
        Quaternion navigationFromCameraT)
    {
        var cameraTFromCamera0 = CameraTFromCamera0Rotation(
            navigationFromCamera0,
            navigationFromCameraT);
        return new Pose(
            cameraTFromCamera0 * camera0FromBoard.position,
            Quaternion.Normalize(cameraTFromCamera0 * camera0FromBoard.rotation));
    }

    /// <summary>
    /// Predicts T_cameraT_board from the validated rotation-only model plus a
    /// diagnostic camera displacement in navigation NED. The displacement is
    /// negated because a stationary board moves opposite to the camera.
    /// </summary>
    public static Pose PredictCameraFromBoardWithNavigationTranslation(
        Pose camera0FromBoard,
        Quaternion navigationFromCamera0,
        Quaternion navigationFromCameraT,
        Vector3 cameraDisplacementInNavigation)
    {
        var rotationOnly = PredictCameraFromBoardRotationOnly(
            camera0FromBoard,
            navigationFromCamera0,
            navigationFromCameraT);
        var cameraTFromNavigation = Quaternion.Inverse(navigationFromCameraT);
        return new Pose(
            rotationOnly.position - cameraTFromNavigation * cameraDisplacementInNavigation,
            rotationOnly.rotation);
    }

    /// <summary>
    /// Derives the physical camera displacement in navigation coordinates from
    /// two visual T_camera_board poses while the board is stationary. This is
    /// diagnostic-only: it does not establish a navigation origin or render a
    /// telemetry position.
    ///
    /// T_navigation_board - T_navigation_camera.position equals
    /// R_navigation_camera * T_camera_board.position at either instant, so
    /// the unknown stationary board position cancels between the two poses.
    /// </summary>
    public static Vector3 CameraDisplacementInNavigationFromStationaryBoard(
        Pose camera0FromBoard,
        Quaternion navigationFromCamera0,
        Pose camera1FromBoard,
        Quaternion navigationFromCamera1)
    {
        var navigationBoardMinusCamera0 =
            navigationFromCamera0 * camera0FromBoard.position;
        var navigationBoardMinusCamera1 =
            navigationFromCamera1 * camera1FromBoard.position;
        return navigationBoardMinusCamera0 - navigationBoardMinusCamera1;
    }

    /// <summary>
    /// Integrates timestamped NED velocity with trapezoidal segments. Boundary
    /// velocities are supplied by the provider's existing interpolation lookup;
    /// no extrapolation is performed here.
    /// </summary>
    public static bool TryIntegrateNavigationVelocityTrapezoidal(
        IReadOnlyList<DjiCameraPoseProvider.TelemetryVelocitySample> velocityHistory,
        long startTimestampNs,
        Vector3 startVelocityInNavigation,
        long endTimestampNs,
        Vector3 endVelocityInNavigation,
        long maximumGapNs,
        out Vector3 displacementInNavigation,
        out string reason)
    {
        displacementInNavigation = Vector3.zero;
        reason = string.Empty;
        if (startTimestampNs <= 0L || endTimestampNs <= 0L)
        {
            reason = "INVALID_TIMESTAMP";
            return false;
        }

        if (endTimestampNs < startTimestampNs)
        {
            reason = "TARGET_BEFORE_BASELINE";
            return false;
        }

        if (maximumGapNs <= 0L)
        {
            reason = "INVALID_MAXIMUM_GAP";
            return false;
        }

        if (!IsFinite(startVelocityInNavigation) || !IsFinite(endVelocityInNavigation))
        {
            reason = "NONFINITE_BOUNDARY_VELOCITY";
            return false;
        }

        if (endTimestampNs == startTimestampNs)
            return true;

        var previousTimestampNs = startTimestampNs;
        var previousVelocity = startVelocityInNavigation;
        if (velocityHistory != null)
        {
            foreach (var sample in velocityHistory)
            {
                if (sample.timestampNs <= startTimestampNs || sample.timestampNs >= endTimestampNs)
                    continue;

                if (sample.timestampNs <= previousTimestampNs)
                {
                    reason = "NON_MONOTONIC_HISTORY";
                    return false;
                }

                if (!IsFinite(sample.navigationVelocityMetersPerSecond))
                {
                    reason = "NONFINITE_HISTORY_VELOCITY";
                    return false;
                }

                if (!TryAccumulateTrapezoid(
                        previousTimestampNs,
                        previousVelocity,
                        sample.timestampNs,
                        sample.navigationVelocityMetersPerSecond,
                        maximumGapNs,
                        ref displacementInNavigation,
                        out reason))
                {
                    return false;
                }

                previousTimestampNs = sample.timestampNs;
                previousVelocity = sample.navigationVelocityMetersPerSecond;
            }
        }

        return TryAccumulateTrapezoid(
            previousTimestampNs,
            previousVelocity,
            endTimestampNs,
            endVelocityInNavigation,
            maximumGapNs,
            ref displacementInNavigation,
            out reason);
    }

    /// <summary>
    /// Integrates the event-driven DJI velocity history as a zero-order hold:
    /// each callback velocity remains active until its successor. This is used
    /// only by translation shadow diagnostics; it does not control rendering.
    /// </summary>
    public static bool TryIntegrateNavigationVelocityStateHistory(
        IReadOnlyList<DjiCameraPoseProvider.TelemetryVelocitySample> velocityHistory,
        long startTimestampNs,
        long endTimestampNs,
        long maximumSampleAgeNs,
        out Vector3 displacementInNavigation,
        out VelocityStateHistoryCoverage coverage,
        out string reason)
    {
        displacementInNavigation = Vector3.zero;
        coverage = default;
        reason = string.Empty;
        if (startTimestampNs <= 0L || endTimestampNs <= 0L)
        {
            reason = "INVALID_TIMESTAMP";
            return false;
        }

        if (endTimestampNs < startTimestampNs)
        {
            reason = "TARGET_BEFORE_BASELINE";
            return false;
        }

        if (maximumSampleAgeNs <= 0L)
        {
            reason = "INVALID_MAXIMUM_SAMPLE_AGE";
            return false;
        }

        if (velocityHistory == null || velocityHistory.Count == 0)
        {
            reason = "BASELINE_NO_SAMPLE";
            return false;
        }

        var previousHistoryTimestampNs = 0L;
        var baselineSampleIndex = -1;
        for (var index = 0; index < velocityHistory.Count; index++)
        {
            var sample = velocityHistory[index];
            if (sample.timestampNs <= 0L ||
                (previousHistoryTimestampNs > 0L && sample.timestampNs <= previousHistoryTimestampNs))
            {
                reason = "NON_MONOTONIC_HISTORY";
                return false;
            }

            if (!IsFinite(sample.navigationVelocityMetersPerSecond))
            {
                reason = "NONFINITE_HISTORY_VELOCITY";
                return false;
            }

            previousHistoryTimestampNs = sample.timestampNs;
            if (sample.timestampNs <= startTimestampNs)
                baselineSampleIndex = index;
        }

        if (baselineSampleIndex < 0)
        {
            reason = "BASELINE_NO_SAMPLE";
            return false;
        }

        var baselineSample = velocityHistory[baselineSampleIndex];
        var baselineSampleTimestampNs = baselineSample.timestampNs;
        if (startTimestampNs - baselineSampleTimestampNs > maximumSampleAgeNs)
        {
            coverage = new VelocityStateHistoryCoverage(
                baselineSampleTimestampNs,
                baselineSampleTimestampNs,
                0);
            reason = "BASELINE_STALE";
            return false;
        }

        var activeVelocity = baselineSample.navigationVelocityMetersPerSecond;
        var activeSampleTimestampNs = baselineSampleTimestampNs;
        var cursorTimestampNs = startTimestampNs;
        var callbackCountInsideInterval = 0;
        for (var index = baselineSampleIndex + 1; index < velocityHistory.Count; index++)
        {
            var sample = velocityHistory[index];
            if (sample.timestampNs > endTimestampNs)
                break;

            if (sample.timestampNs - activeSampleTimestampNs > maximumSampleAgeNs)
            {
                coverage = new VelocityStateHistoryCoverage(
                    baselineSampleTimestampNs,
                    activeSampleTimestampNs,
                    callbackCountInsideInterval);
                reason = "INTERNAL_GAP_STALE";
                return false;
            }

            displacementInNavigation += activeVelocity *
                                         ((sample.timestampNs - cursorTimestampNs) / 1_000_000_000f);
            cursorTimestampNs = sample.timestampNs;
            activeVelocity = sample.navigationVelocityMetersPerSecond;
            activeSampleTimestampNs = sample.timestampNs;
            callbackCountInsideInterval++;
        }

        if (endTimestampNs - activeSampleTimestampNs > maximumSampleAgeNs)
        {
            coverage = new VelocityStateHistoryCoverage(
                baselineSampleTimestampNs,
                activeSampleTimestampNs,
                callbackCountInsideInterval);
            reason = "TARGET_STALE";
            return false;
        }

        displacementInNavigation += activeVelocity *
                                     ((endTimestampNs - cursorTimestampNs) / 1_000_000_000f);
        coverage = new VelocityStateHistoryCoverage(
            baselineSampleTimestampNs,
            activeSampleTimestampNs,
            callbackCountInsideInterval);
        return true;
    }

    private static bool TryAccumulateTrapezoid(
        long firstTimestampNs,
        Vector3 firstVelocity,
        long secondTimestampNs,
        Vector3 secondVelocity,
        long maximumGapNs,
        ref Vector3 displacement,
        out string reason)
    {
        reason = string.Empty;
        var intervalNs = secondTimestampNs - firstTimestampNs;
        if (intervalNs <= 0L)
        {
            reason = "NON_MONOTONIC_HISTORY";
            return false;
        }

        if (intervalNs > maximumGapNs)
        {
            reason = "HISTORY_GAP_TOO_LARGE";
            return false;
        }

        var intervalSeconds = intervalNs / 1_000_000_000f;
        displacement += 0.5f * (firstVelocity + secondVelocity) * intervalSeconds;
        return true;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

    public static bool ValidateWorldBoardRoundTrip(Pose worldFromBoard, out string result)
    {
        var worldFromBoardMatrix = WorldFromBoard(worldFromBoard);
        var boardFromWorldMatrix = BoardFromWorld(worldFromBoard);
        var identityError = MatrixDifference(worldFromBoardMatrix * boardFromWorldMatrix, Matrix4x4.identity);
        var points = new[]
        {
            Vector3.zero,
            new Vector3(0.04f, -0.03f, 0.02f),
            new Vector3(-0.08f, 0.06f, -0.01f)
        };
        var maximumRoundTripError = 0f;
        foreach (var boardPoint in points)
        {
            var worldPoint = worldFromBoardMatrix.MultiplyPoint3x4(boardPoint);
            var roundTrippedBoardPoint = boardFromWorldMatrix.MultiplyPoint3x4(worldPoint);
            maximumRoundTripError = Mathf.Max(maximumRoundTripError, Vector3.Distance(boardPoint, roundTrippedBoardPoint));
        }

        var valid = identityError <= MatrixTolerance && maximumRoundTripError <= MatrixTolerance;
        result = $"identityError={identityError:E3} maxPointRoundTripError={maximumRoundTripError:E3} valid={valid}";
        return valid;
    }

    /// <summary>Verifies T_camera_board * T_board_camera = identity.</summary>
    public static bool ValidateCameraBoardRoundTrip(Pose cameraFromBoard, out string result)
    {
        var cameraFromBoardMatrix = MatrixFromPose(cameraFromBoard);
        var boardFromCameraMatrix = cameraFromBoardMatrix.inverse;
        var identityError = MatrixDifference(cameraFromBoardMatrix * boardFromCameraMatrix, Matrix4x4.identity);
        var valid = identityError <= MatrixTolerance;
        result = $"identityError={identityError:E3} valid={valid}";
        return valid;
    }

    private static float MatrixDifference(Matrix4x4 first, Matrix4x4 second)
    {
        var maximumDifference = 0f;
        for (var row = 0; row < 4; row++)
        for (var column = 0; column < 4; column++)
            maximumDifference = Mathf.Max(maximumDifference, Mathf.Abs(first[row, column] - second[row, column]));

        return maximumDifference;
    }
}
