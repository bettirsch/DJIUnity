using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Selects whether the render-only visual temporal predictor is active. This
/// does not participate in visual localization, outage propagation, or GNSS.
/// </summary>
public enum VisualTemporalCompensationMode
{
    Disabled,
    Enabled
}

[Serializable]
public sealed class VisualTemporalCompensationConfiguration
{
    [Min(1f)] public float maxVisualPredictionMilliseconds = 100f;
    [Min(1f)] public float maxDisplayedTimingAgeMilliseconds = 150f;
    [Min(1f)] public float maxVisualSampleIntervalMilliseconds = 300f;
    [Min(0.01f)] public float maxLinearVelocityMetersPerSecond = 5f;
    [Min(1f)] public float maxAngularVelocityDegreesPerSecond = 180f;
}

/// <summary>
/// Immutable diagnostic description of the pose actually sent to the visual
/// overlay. rawVisualPose always remains the authoritative localization pose.
/// </summary>
public readonly struct VisualTemporalCompensationSnapshot
{
    public readonly VisualTemporalCompensationMode mode;
    public readonly int segmentId;
    public readonly bool predictionValid;
    public readonly float predictionHorizonMilliseconds;
    public readonly Vector3 estimatedLinearVelocity;
    public readonly float estimatedAngularVelocityDegreesPerSecond;
    public readonly Pose rawVisualPose;
    public readonly Pose predictedRenderPose;
    public readonly float translationPredictionMagnitudeMeters;
    public readonly float rotationPredictionMagnitudeDegrees;
    public readonly string reason;

    public VisualTemporalCompensationSnapshot(
        VisualTemporalCompensationMode mode,
        int segmentId,
        bool predictionValid,
        float predictionHorizonMilliseconds,
        Vector3 estimatedLinearVelocity,
        float estimatedAngularVelocityDegreesPerSecond,
        Pose rawVisualPose,
        Pose predictedRenderPose,
        string reason)
    {
        this.mode = mode;
        this.segmentId = segmentId;
        this.predictionValid = predictionValid;
        this.predictionHorizonMilliseconds = predictionHorizonMilliseconds;
        this.estimatedLinearVelocity = estimatedLinearVelocity;
        this.estimatedAngularVelocityDegreesPerSecond = estimatedAngularVelocityDegreesPerSecond;
        this.rawVisualPose = rawVisualPose;
        this.predictedRenderPose = predictedRenderPose;
        translationPredictionMagnitudeMeters = Vector3.Distance(rawVisualPose.position, predictedRenderPose.position);
        rotationPredictionMagnitudeDegrees = Quaternion.Angle(rawVisualPose.rotation, predictedRenderPose.rotation);
        this.reason = reason;
    }

    public static VisualTemporalCompensationSnapshot Uncompensated(
        VisualTemporalCompensationMode mode,
        int segmentId,
        Pose rawVisualPose,
        string reason) =>
        new(mode, segmentId, false, float.NaN, Vector3.zero, float.NaN, rawVisualPose, rawVisualPose, reason);
}

/// <summary>
/// Bounded two-sample extrapolator for T_camera_board. Source-frame times are
/// used for all motion estimates; application time is retained only for audit.
/// </summary>
public sealed class VisualTemporalCompensationPredictor
{
    private readonly List<Sample> _samples = new(3);

    private struct Sample
    {
        public long sourceFrameId;
        public long sourceTimestampNs;
        public long poseApplicationTimestampNs;
        public Pose pose;
    }

    public int SampleCount => _samples.Count;

    public void Clear() => _samples.Clear();

    public void AddAcceptedVisualPose(long sourceFrameId, long sourceTimestampNs, Pose pose)
    {
        if (sourceFrameId <= 0L || sourceTimestampNs <= 0L || !IsFinite(pose))
            return;

        if (_samples.Count > 0)
        {
            var latest = _samples[^1];
            if (sourceFrameId <= latest.sourceFrameId || sourceTimestampNs <= latest.sourceTimestampNs)
                return;
        }

        _samples.Add(new Sample
        {
            sourceFrameId = sourceFrameId,
            sourceTimestampNs = sourceTimestampNs,
            pose = pose
        });
        if (_samples.Count > 3)
            _samples.RemoveAt(0);
    }

    public void RecordApplicationTimestamp(long sourceFrameId, long poseApplicationTimestampNs)
    {
        if (sourceFrameId <= 0L || poseApplicationTimestampNs <= 0L)
            return;

        for (var index = _samples.Count - 1; index >= 0; index--)
        {
            if (_samples[index].sourceFrameId != sourceFrameId)
                continue;

            var sample = _samples[index];
            sample.poseApplicationTimestampNs = poseApplicationTimestampNs;
            _samples[index] = sample;
            return;
        }
    }

    public bool TryPredict(
        long currentSourceFrameId,
        long displayedTimestampNs,
        long nowTimestampNs,
        VisualTemporalCompensationConfiguration configuration,
        out Pose predictedPose,
        out float horizonMilliseconds,
        out Vector3 linearVelocity,
        out float angularVelocityDegreesPerSecond,
        out string reason)
    {
        predictedPose = Pose.identity;
        horizonMilliseconds = float.NaN;
        linearVelocity = Vector3.zero;
        angularVelocityDegreesPerSecond = float.NaN;
        reason = string.Empty;
        if (configuration == null)
        {
            reason = "CONFIGURATION_UNAVAILABLE";
            return false;
        }

        if (_samples.Count < 2)
        {
            reason = "INSUFFICIENT_NEW_VISUAL_SAMPLES";
            return false;
        }

        var current = _samples[^1];
        var previous = _samples[^2];
        if (current.sourceFrameId != currentSourceFrameId)
        {
            reason = "CURRENT_VISUAL_SAMPLE_NOT_RECORDED";
            return false;
        }

        if (displayedTimestampNs <= 0L || nowTimestampNs <= 0L ||
            nowTimestampNs < displayedTimestampNs)
        {
            reason = "OES_TIMING_UNAVAILABLE";
            return false;
        }

        var timingAgeMilliseconds = (nowTimestampNs - displayedTimestampNs) / 1_000_000f;
        if (timingAgeMilliseconds > configuration.maxDisplayedTimingAgeMilliseconds)
        {
            reason = "OES_TIMING_STALE";
            return false;
        }

        var horizonNs = displayedTimestampNs - current.sourceTimestampNs;
        if (horizonNs < 0L)
        {
            reason = "NEGATIVE_HORIZON";
            return false;
        }

        horizonMilliseconds = horizonNs / 1_000_000f;
        if (horizonMilliseconds > configuration.maxVisualPredictionMilliseconds)
        {
            reason = "HORIZON_EXCEEDS_MAXIMUM";
            return false;
        }

        var sourceIntervalNs = current.sourceTimestampNs - previous.sourceTimestampNs;
        if (sourceIntervalNs <= 0L)
        {
            reason = "NON_MONOTONIC_VISUAL_TIMESTAMP";
            return false;
        }

        var sourceIntervalMilliseconds = sourceIntervalNs / 1_000_000f;
        if (sourceIntervalMilliseconds > configuration.maxVisualSampleIntervalMilliseconds)
        {
            reason = "VISUAL_SAMPLE_GAP_TOO_LARGE";
            return false;
        }

        var sourceIntervalSeconds = sourceIntervalNs / 1_000_000_000f;
        linearVelocity = (current.pose.position - previous.pose.position) / sourceIntervalSeconds;
        var rotationDelta = NormalizeShortest(current.pose.rotation * Quaternion.Inverse(previous.pose.rotation));
        var rotationDeltaDegrees = Quaternion.Angle(Quaternion.identity, rotationDelta);
        angularVelocityDegreesPerSecond = rotationDeltaDegrees / sourceIntervalSeconds;
        if (!IsFinite(linearVelocity) || !float.IsFinite(angularVelocityDegreesPerSecond) ||
            linearVelocity.magnitude > configuration.maxLinearVelocityMetersPerSecond ||
            angularVelocityDegreesPerSecond > configuration.maxAngularVelocityDegreesPerSecond)
        {
            reason = "VISUAL_MOTION_SANITY_LIMIT";
            return false;
        }

        var horizonSeconds = horizonNs / 1_000_000_000f;
        var rotationAdvance = Quaternion.SlerpUnclamped(
            Quaternion.identity,
            rotationDelta,
            horizonSeconds / sourceIntervalSeconds);
        predictedPose = new Pose(
            current.pose.position + linearVelocity * horizonSeconds,
            NormalizeShortest(rotationAdvance * current.pose.rotation));
        return true;
    }

    private static Quaternion NormalizeShortest(Quaternion value)
    {
        var normalized = Quaternion.Normalize(value);
        return normalized.w < 0f
            ? new Quaternion(-normalized.x, -normalized.y, -normalized.z, -normalized.w)
            : normalized;
    }

    private static bool IsFinite(Pose pose) =>
        IsFinite(pose.position) &&
        float.IsFinite(pose.rotation.x) && float.IsFinite(pose.rotation.y) &&
        float.IsFinite(pose.rotation.z) && float.IsFinite(pose.rotation.w);

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
}
