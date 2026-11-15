using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Writes a bounded-rate, diagnostic-only telemetry timeline for one
/// DroneView session. It consumes already-polled Unity telemetry and never
/// registers an additional DJI listener or participates in pose rendering.
/// </summary>
[DisallowMultipleComponent]
public sealed partial class DjiTelemetryValidationLogger : MonoBehaviour
{
    [SerializeField, Min(100)] private int telemetryValidationSnapshotIntervalMs = 500;
    [SerializeField, Min(1)] private int flushEverySamples = 10;

    private DjiBoardVisionProvider _boardVisionProvider;
    private DjiCameraPoseProvider _telemetryProvider;
    private DjiWorldLocalizationShadow _worldLocalizationShadow;
    private StreamWriter _writer;
    private StreamWriter _outageWriter;
    private TextWriter _visualLatencyWriter;
    private string _path;
    private string _outagePath;
    private string _visualLatencyPath;
    private string _telemetrySessionId;
    private long _nextSnapshotTimestampNs;
    private long _nextWorldShadowDiagnosticsTimestampNs;
    private int _sampleIndex;
    private int _outageEventCount;
    private int _visualLatencySampleCount;
    private int _worldRotationPropagationEquivalenceRecordCount;
    private int _worldHorizontalGnssShadowRecordCount;
    private int _horizontalQualityCallbackRecordCount;
    private long _lastVisualLatencySourceFrameId;
    private long _lastVisualLatencySourceTimestampNs;
    private readonly HorizontalQualitySampleDeduplicator _horizontalQualitySampleDeduplicator = new();
    private bool _writeFailureLogged;

    public string TelemetrySessionId => _telemetrySessionId;

    public void Configure(DjiBoardVisionProvider boardVisionProvider, DjiCameraPoseProvider telemetryProvider)
    {
        _boardVisionProvider = boardVisionProvider;
        _telemetryProvider = telemetryProvider;
    }

    private void Start() => StartSession();

    private void Update()
    {
        if (_writer == null)
            return;

        var nowTimestampNs = DjiBoardVisionBridge.GetMonotonicTimestampNs();
        if (nowTimestampNs <= 0L)
            return;

        DrainRelativePositionExperiment(nowTimestampNs);
        WriteNewHorizontalQualitySamples(nowTimestampNs);
        if (nowTimestampNs < _nextSnapshotTimestampNs)
            return;

        // Skip missed slots rather than burst-writing old snapshots after a stall.
        _nextSnapshotTimestampNs = nowTimestampNs + telemetryValidationSnapshotIntervalMs * 1_000_000L;
        WriteSnapshot(nowTimestampNs);
        WriteWorldShadowDiagnostics(nowTimestampNs, finalSummary: false);
    }

    private void OnDisable() => StopSession();

    private void OnApplicationQuit() => StopSession();

    private void StartSession()
    {
        if (_writer != null)
            return;

        try
        {
            var directory = Application.persistentDataPath;
            Directory.CreateDirectory(directory);
            _telemetrySessionId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture);
            var fileName = $"dji-telemetry-validation-{_telemetrySessionId}.jsonl";
            _path = Path.Combine(directory, fileName);
            var stream = new FileStream(_path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096);
            _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 4096);
            _outagePath = Path.Combine(directory, $"dji-outage-events-{_telemetrySessionId}.jsonl");
            var outageStream = new FileStream(_outagePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096);
            _outageWriter = new StreamWriter(outageStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 4096);
            _visualLatencyPath = Path.Combine(directory, $"dji-visual-latency-{_telemetrySessionId}.jsonl");
            var visualLatencyStream = new FileStream(_visualLatencyPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096);
            _visualLatencyWriter = new BoundedDiagnosticJsonlWriter(
                new StreamWriter(visualLatencyStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 4096));
            _sampleIndex = 0;
            _outageEventCount = 0;
            _visualLatencySampleCount = 0;
            _worldRotationPropagationEquivalenceRecordCount = 0;
            _worldHorizontalGnssShadowRecordCount = 0;
            _horizontalQualityCallbackRecordCount = 0;
            _lastVisualLatencySourceFrameId = 0L;
            _lastVisualLatencySourceTimestampNs = 0L;
            _nextSnapshotTimestampNs = DjiBoardVisionBridge.GetMonotonicTimestampNs();
            _nextWorldShadowDiagnosticsTimestampNs = _nextSnapshotTimestampNs;
            StartMotionDiagnostics();
            DjiDetectorCaptureDiagnostics.Configure(_telemetrySessionId, true);
            var rawCallbackCaptureStarted = DJIPoseProvider.StartRawTelemetryCallbackLogging(_telemetrySessionId);
            StartRelativePositionExperiment();
            Debug.Log(
                "DJI_TELEMETRY_VALIDATION_LOG_STARTED " +
                $"path={_path} intervalMs={telemetryValidationSnapshotIntervalMs} format=JSONL " +
                $"outageEventsPath={_outagePath} visualLatencyPath={_visualLatencyPath} " +
                $"telemetrySessionId={_telemetrySessionId} rawCallbackCaptureStarted={rawCallbackCaptureStarted} " +
                "clockDomain=ANDROID_MONOTONIC");
        }
        catch (Exception exception)
        {
            LogWriteFailure(exception);
        }
    }

    private void StopSession()
    {
        DjiDetectorCaptureDiagnostics.Configure(_telemetrySessionId, false);
        StopMotionDiagnostics();
        StopRelativePositionExperiment();
        if (_writer == null)
            return;

        try
        {
            WriteWorldShadowDiagnostics(
                DjiBoardVisionBridge.GetMonotonicTimestampNs(),
                finalSummary: true);
            DJIPoseProvider.StopRawTelemetryCallbackLogging();
            _writer.Flush();
            _writer.Dispose();
            _outageWriter?.Flush();
            _outageWriter?.Dispose();
            _visualLatencyWriter?.Flush();
            _visualLatencyWriter?.Dispose();
            if (_visualLatencyWriter is BoundedDiagnosticJsonlWriter buffered &&
                (buffered.Dropped > 0 || buffered.WriteErrors > 0))
                Debug.LogWarning($"DJI_ORIENTATION_DIAGNOSTIC_INCOMPLETE queueDrops={buffered.Dropped} fileWriteErrors={buffered.WriteErrors}");
            Debug.Log(
                $"DJI_TELEMETRY_VALIDATION_LOG_STOPPED samples={_sampleIndex} outageEvents={_outageEventCount} visualLatencySamples={_visualLatencySampleCount} worldRotationPropagationEquivalenceRecords={_worldRotationPropagationEquivalenceRecordCount} worldHorizontalGnssShadowRecords={_worldHorizontalGnssShadowRecordCount} " +
                $"sessionId={_telemetrySessionId} path={_path} outageEventsPath={_outagePath} visualLatencyPath={_visualLatencyPath}");
        }
        catch (Exception exception)
        {
            LogWriteFailure(exception);
        }
        finally
        {
            _writer = null;
            _outageWriter = null;
            _visualLatencyWriter = null;
            _path = null;
            _outagePath = null;
            _visualLatencyPath = null;
            _telemetrySessionId = null;
        }
    }

    /// <summary>
    /// Writes one latency record for a newly authoritative visual pose. This
    /// deliberately does not claim that the CPU ImageReader frame and the OES
    /// texture latch represent the same decoded DJI video frame: MSDK exposes
    /// neither a shared PTS nor a shared source-frame identifier to this app.
    /// </summary>
    public void WriteVisualLatencySample(
        DjiBoardVisionProvider.VisualPoseTimingSample visualPose,
        long poseAppliedByUnityTimestampNs,
        DJIGPUBackground.DisplayedFrameTiming displayedFrameTiming,
        VisualTemporalCompensationSnapshot temporalCompensation,
        VisualRenderPathDiagnostics visualRenderPath)
    {
        if (_visualLatencyWriter == null || !visualPose.IsValid ||
            visualPose.quality != DjiBoardVisionProvider.VisualPoseQuality.HighConfidence ||
            poseAppliedByUnityTimestampNs <= 0L ||
            visualPose.sourceFrameTimestampNs < _lastVisualLatencySourceTimestampNs ||
            (visualPose.sourceFrameTimestampNs == _lastVisualLatencySourceTimestampNs &&
             visualPose.sourceFrameId <= _lastVisualLatencySourceFrameId))
        {
            return;
        }

        try
        {
            var json = new JsonLine();
            json.BeginObject();
            json.Property("telemetrySessionId").String(_telemetrySessionId ?? "UNAVAILABLE");
            json.Property("orientationCandidateId").Number(visualPose.sourceFrameTimestampNs);
            json.Property("clockDomain").String("ANDROID_MONOTONIC_elapsedRealtimeNanos");
            json.Property("cpuDetectorFrameSequence").Number(visualPose.sourceFrameId);
            json.Property("cpuDetectorSequenceReliable").Boolean(true);
            json.Property("cpuDetectorSequenceScope").String("DETECTOR_GENERATION_NOT_SESSION_GLOBAL");
            json.Property("measurementIdentity").String("ANDROID_CALLBACK_TIMESTAMP_AND_CPU_DETECTOR_SEQUENCE");
            json.Property("djiDecodedSourceFrameIdAvailable").Boolean(false);
            json.Property("sourceFrameTimestampNs").Number(visualPose.sourceFrameTimestampNs);
            json.Property("sourceFrameTimestampMeaning").String("CAMERA_FRAME_LISTENER_CALLBACK_ARRIVAL");
            json.Property("detectorStartTimestampNs").Number(visualPose.detectorStartTimestampNs);
            json.Property("detectorEndTimestampNs").Number(visualPose.detectorEndTimestampNs);
            json.Property("pnpCompletedTimestampNs").Number(visualPose.pnpCompletedTimestampNs);
            json.Property("pnpTimingGranularity").String("COUPLED_NATIVE_APRILTAG_AND_PNP_CALL");
            json.Property("poseDeliveredToUnityTimestampNs").Number(visualPose.poseDeliveredToUnityTimestampNs);
            json.Property("poseAppliedByUnityTimestampNs").Number(poseAppliedByUnityTimestampNs);
            json.Property("detectorQueueDelayMs").NullableNumber(
                SampleAgeMilliseconds(visualPose.detectorStartTimestampNs, visualPose.sourceFrameTimestampNs));
            json.Property("nativeAprilTagAndPnpDurationMs").NullableNumber(
                SampleAgeMilliseconds(visualPose.pnpCompletedTimestampNs, visualPose.detectorStartTimestampNs));
            json.Property("bridgeToUnityDelayMs").NullableNumber(
                SampleAgeMilliseconds(visualPose.poseDeliveredToUnityTimestampNs, visualPose.pnpCompletedTimestampNs));
            json.Property("totalSourceToPoseAppliedMs").NullableNumber(
                SampleAgeMilliseconds(poseAppliedByUnityTimestampNs, visualPose.sourceFrameTimestampNs));
            json.Property("displayedFrameArrivalSequence").NullableNumber(
                displayedFrameTiming.HasFrameAvailableTimestamp ? displayedFrameTiming.oesFrameAvailableSequence : (long?)null);
            json.Property("displayedFrameArrivalTimestampNs").NullableNumber(
                displayedFrameTiming.HasFrameAvailableTimestamp ? displayedFrameTiming.oesFrameAvailableTimestampNs : (long?)null);
            json.Property("displayedFrameArrivalMeaning").String(
                "SURFACE_TEXTURE_ON_FRAME_AVAILABLE_NATIVE_CLOCK_BOOTTIME");
            json.Property("displayedOesLatchSequence").NullableNumber(
                displayedFrameTiming.HasLatchTimestamp ? displayedFrameTiming.oesLatchSequence : (long?)null);
            json.Property("displayedOesLatchTimestampNs").NullableNumber(
                displayedFrameTiming.HasLatchTimestamp ? displayedFrameTiming.oesLatchTimestampNs : (long?)null);
            json.Property("displayedFrameIdReliable").Boolean(false);
            json.Property("displayedTimestampMeaning").String("SURFACE_TEXTURE_ARRIVAL_OR_OES_LATCH_NOT_DJI_SOURCE_PTS");
            var displayedFrameTimestampNs = displayedFrameTiming.HasFrameAvailableTimestamp
                ? displayedFrameTiming.oesFrameAvailableTimestampNs
                : displayedFrameTiming.HasLatchTimestamp
                    ? displayedFrameTiming.oesLatchTimestampNs
                    : 0L;
            json.Property("displayedVsPoseSkewMs").NullableNumber(displayedFrameTimestampNs > 0L
                ? (double?)((displayedFrameTimestampNs - visualPose.sourceFrameTimestampNs) / 1_000_000.0)
                : null);
            json.Property("oesLatchMinusCpuCallbackMs").NullableNumber(displayedFrameTiming.HasLatchTimestamp
                ? (double?)((displayedFrameTiming.oesLatchTimestampNs - visualPose.sourceFrameTimestampNs) / 1_000_000.0)
                : null);
            json.Property("sameDecodedFrameProven").Boolean(false);
            json.Property("markerCount").Number(visualPose.markerCount);
            json.Property("reprojectionRmsPx").Number(visualPose.reprojectionRmsPixels);
            json.Property("visualPoseQuality").String(visualPose.quality.ToString());
            json.Property("currentRenderSource").String(DjiRenderPoseSource.Visual.ToString());
            json.Property("temporalCompensationMode").String(temporalCompensation.mode.ToString());
            json.Property("temporalSegmentId").Number(temporalCompensation.segmentId);
            json.Property("predictionValid").Boolean(temporalCompensation.predictionValid);
            json.Property("predictionHorizonMs").NullableNumber(
                temporalCompensation.predictionValid ? temporalCompensation.predictionHorizonMilliseconds : (float?)null);
            json.Property("estimatedLinearVelocityX").NullableNumber(
                temporalCompensation.predictionValid ? temporalCompensation.estimatedLinearVelocity.x : (float?)null);
            json.Property("estimatedLinearVelocityY").NullableNumber(
                temporalCompensation.predictionValid ? temporalCompensation.estimatedLinearVelocity.y : (float?)null);
            json.Property("estimatedLinearVelocityZ").NullableNumber(
                temporalCompensation.predictionValid ? temporalCompensation.estimatedLinearVelocity.z : (float?)null);
            json.Property("estimatedAngularVelocityDegPerSec").NullableNumber(
                temporalCompensation.predictionValid ? temporalCompensation.estimatedAngularVelocityDegreesPerSecond : (float?)null);
            json.Property("temporalPredictionReason").String(temporalCompensation.reason ?? string.Empty);
            json.Property("visualRenderPath").String(visualRenderPath.selectedPath.ToString());
            json.Property("visualRenderPathSegmentId").Number(visualRenderPath.segmentId);
            json.Property("worldRenderPathEligible").Boolean(visualRenderPath.worldPathEligible);
            json.Property("worldRenderPathUsed").Boolean(visualRenderPath.worldPathUsed);
            json.Property("worldRenderFallbackReason").String(visualRenderPath.fallbackReason ?? string.Empty);
            json.Property("worldRenderFallbackCount").Number(visualRenderPath.fallbackCount);
            WriteTemporalPose(json, "rawVisualPosition", "rawVisualRotation", temporalCompensation.rawVisualPose);
            WriteTemporalPose(json, "predictedRenderPosition", "predictedRenderRotation", temporalCompensation.predictedRenderPose);
            json.Property("translationPredictionMagnitudeM").Number(temporalCompensation.translationPredictionMagnitudeMeters);
            json.Property("rotationPredictionMagnitudeDeg").Number(temporalCompensation.rotationPredictionMagnitudeDegrees);
            json.Property("cameraFromBoard").BeginObject();
            json.Property("positionX").Number(visualPose.cameraFromBoard.position.x);
            json.Property("positionY").Number(visualPose.cameraFromBoard.position.y);
            json.Property("positionZ").Number(visualPose.cameraFromBoard.position.z);
            json.Property("rotationX").Number(visualPose.cameraFromBoard.rotation.x);
            json.Property("rotationY").Number(visualPose.cameraFromBoard.rotation.y);
            json.Property("rotationZ").Number(visualPose.cameraFromBoard.rotation.z);
            json.Property("rotationW").Number(visualPose.cameraFromBoard.rotation.w);
            json.Property("transformConvention").String("T_camera_board");
            json.EndObject();
            json.EndObject();

            _visualLatencyWriter.WriteLine(json.ToString());
            _visualLatencySampleCount++;
            // Callback time survives detector restarts. Sequence is only a
            // tie-breaker for distinct callbacks sharing a clock tick.
            _lastVisualLatencySourceTimestampNs = visualPose.sourceFrameTimestampNs;
            _lastVisualLatencySourceFrameId = visualPose.sourceFrameId;
            if (_visualLatencySampleCount % flushEverySamples == 0)
                _visualLatencyWriter.Flush();

            if (_visualLatencySampleCount == 1 || _visualLatencySampleCount % 10 == 0)
            {
                Debug.Log(
                    "DJI_VISUAL_LATENCY " +
                    $"sourceFrameId={visualPose.sourceFrameId} sourceToApplyMs={FormatLatency(SampleAgeMilliseconds(poseAppliedByUnityTimestampNs, visualPose.sourceFrameTimestampNs))} " +
                    $"oesArrivalSequence={(displayedFrameTiming.HasFrameAvailableTimestamp ? displayedFrameTiming.oesFrameAvailableSequence : 0L)} " +
                    $"oesLatchSequence={(displayedFrameTiming.HasLatchTimestamp ? displayedFrameTiming.oesLatchSequence : 0L)} " +
                    "sameDecodedFrameProven=false");
            }
        }
        catch (Exception exception)
        {
            LogWriteFailure(exception);
        }
    }

    private static void WriteTemporalPose(JsonLine json, string positionProperty, string rotationProperty, Pose pose)
    {
        json.Property(positionProperty).BeginObject();
        json.Property("x").Number(pose.position.x);
        json.Property("y").Number(pose.position.y);
        json.Property("z").Number(pose.position.z);
        json.EndObject();
        json.Property(rotationProperty).BeginObject();
        json.Property("x").Number(pose.rotation.x);
        json.Property("y").Number(pose.rotation.y);
        json.Property("z").Number(pose.rotation.z);
        json.Property("w").Number(pose.rotation.w);
        json.EndObject();
    }

    private void WriteWorldShadowDiagnostics(long nowTimestampNs, bool finalSummary)
    {
        if (_visualLatencyWriter == null ||
            (!finalSummary && nowTimestampNs < _nextWorldShadowDiagnosticsTimestampNs))
        {
            return;
        }

        _worldLocalizationShadow ??= FindFirstObjectByType<DjiWorldLocalizationShadow>();
        if (_worldLocalizationShadow == null)
        {
            return;
        }

        var wroteRecord = false;
        if (TryBuildWorldRotationPropagationEquivalenceJsonLine(
                _telemetrySessionId,
                _worldLocalizationShadow.RotationPropagationEquivalence,
                finalSummary,
                out var propagationJsonLine))
        {
            _visualLatencyWriter.WriteLine(propagationJsonLine);
            _worldRotationPropagationEquivalenceRecordCount++;
            wroteRecord = true;
        }

        if (TryBuildWorldHorizontalGnssShadowJsonLine(
                _telemetrySessionId,
                _worldLocalizationShadow.HorizontalGnssWorldShadow,
                _worldLocalizationShadow.HorizontalGnssWorldShadowReacquisition,
                _worldLocalizationShadow.HorizontalGnssWorldShadowDiagnostics,
                _worldLocalizationShadow.HorizontalGnssSmoothingShadow,
                _worldLocalizationShadow.HorizontalGnssSmoothingReacquisition,
                _worldLocalizationShadow.ProductionHorizontalGnssApplied,
                _worldLocalizationShadow.ProductionHorizontalGnssState,
                _worldLocalizationShadow.ProductionHorizontalGnssReason,
                _worldLocalizationShadow.HorizontalGnssQualityAdmission,
                _worldLocalizationShadow.ProductionVerticalAltitude,
                finalSummary,
                out var horizontalGnssJsonLine))
        {
            _visualLatencyWriter.WriteLine(horizontalGnssJsonLine);
            _worldHorizontalGnssShadowRecordCount++;
            wroteRecord = true;
        }

        if (!wroteRecord)
            return;

        if (!finalSummary)
            _nextWorldShadowDiagnosticsTimestampNs = nowTimestampNs + 1_000_000_000L;
        _visualLatencyWriter.Flush();
    }

    /// <summary>
    /// Serializes the already-computed production-versus-world rotation-shadow
    /// residual. Invalid snapshots intentionally produce no diagnostic record.
    /// </summary>
    public static bool TryBuildWorldRotationPropagationEquivalenceJsonLine(
        string telemetrySessionId,
        WorldRotationPropagationEquivalenceSnapshot snapshot,
        bool finalSummary,
        out string jsonLine)
    {
        jsonLine = null;
        if (!snapshot.valid)
            return false;

        var json = new JsonLine();
        json.BeginObject();
        json.Property("recordType").String("WORLD_ROTATION_PROPAGATION_EQUIVALENCE");
        json.Property("worldRotationPropagationRecordKind").String(finalSummary ? "FINAL" : "THROTTLED");
        json.Property("telemetrySessionId").String(telemetrySessionId ?? "UNAVAILABLE");
        json.Property("clockDomain").String("ANDROID_MONOTONIC_elapsedRealtimeNanos");
        json.Property("worldRotationPropagationValid").Boolean(snapshot.valid);
        json.Property("worldRotationPropagationSampleCount").Number(snapshot.sampleCount);
        json.Property("worldRotationPropagationTelemetryTimestampNs").Number(snapshot.telemetryTimestampNs);
        json.Property("worldRotationPropagationLatestTranslationResidualM").Number(snapshot.latestTranslationResidualM);
        json.Property("worldRotationPropagationLatestRotationResidualDeg").Number(snapshot.latestRotationResidualDeg);
        json.Property("worldRotationPropagationMaxTranslationResidualM").Number(snapshot.maxTranslationResidualM);
        json.Property("worldRotationPropagationMaxRotationResidualDeg").Number(snapshot.maxRotationResidualDeg);
        json.Property("productionBaselineTimestampNs").Number(snapshot.productionBaselineTimestampNs);
        json.Property("resolvedWorldBaselineTimestampNs").Number(snapshot.resolvedWorldBaselineTimestampNs);
        json.Property("historicalBaselineResolved").Boolean(snapshot.historicalBaselineResolved);
        json.Property("worldBaselineHistoryAgeMs").Number(snapshot.worldBaselineHistoryAgeMs);
        json.Property("worldBaselineHistoryCount").Number(snapshot.worldBaselineHistoryCount);
        json.Property("baselineResolveFailureReason").String(snapshot.baselineResolveFailureReason);
        json.EndObject();
        jsonLine = json.ToString();
        return true;
    }

    /// <summary>
    /// Serializes the coordinator-owned, horizontal-only GNSS candidate. This
    /// is a diagnostic record only and never implies render authority.
    /// </summary>
    public static bool TryBuildWorldHorizontalGnssShadowJsonLine(
        string telemetrySessionId,
        HorizontalGnssWorldShadowSnapshot snapshot,
        HorizontalGnssWorldShadowReacquisitionSnapshot reacquisition,
        HorizontalGnssWorldShadowOutageDiagnostics diagnostics,
        bool finalSummary,
        out string jsonLine) =>
        TryBuildWorldHorizontalGnssShadowJsonLine(
            telemetrySessionId,
            snapshot,
            reacquisition,
            diagnostics,
            default,
            default,
            false,
            HorizontalGnssSmoothingState.Uninitialized,
            string.Empty,
            finalSummary,
            out jsonLine);

    public static bool TryBuildWorldHorizontalGnssShadowJsonLine(
        string telemetrySessionId,
        HorizontalGnssWorldShadowSnapshot snapshot,
        HorizontalGnssWorldShadowReacquisitionSnapshot reacquisition,
        HorizontalGnssWorldShadowOutageDiagnostics diagnostics,
        HorizontalGnssSmoothingShadowSnapshot smoothedSnapshot,
        HorizontalGnssSmoothingReacquisitionSnapshot smoothedReacquisition,
        bool finalSummary,
        out string jsonLine) =>
        TryBuildWorldHorizontalGnssShadowJsonLine(
            telemetrySessionId,
            snapshot,
            reacquisition,
            diagnostics,
            smoothedSnapshot,
            smoothedReacquisition,
            false,
            HorizontalGnssSmoothingState.Uninitialized,
            string.Empty,
            finalSummary,
            out jsonLine);

    public static bool TryBuildWorldHorizontalGnssShadowJsonLine(
        string telemetrySessionId,
        HorizontalGnssWorldShadowSnapshot snapshot,
        HorizontalGnssWorldShadowReacquisitionSnapshot reacquisition,
        HorizontalGnssWorldShadowOutageDiagnostics diagnostics,
        HorizontalGnssSmoothingShadowSnapshot smoothedSnapshot,
        HorizontalGnssSmoothingReacquisitionSnapshot smoothedReacquisition,
        bool productionHorizontalGnssApplied,
        HorizontalGnssSmoothingState productionHorizontalGnssState,
        string productionHorizontalGnssReason,
        bool finalSummary,
        out string jsonLine) =>
        TryBuildWorldHorizontalGnssShadowJsonLine(
            telemetrySessionId,
            snapshot,
            reacquisition,
            diagnostics,
            smoothedSnapshot,
            smoothedReacquisition,
            productionHorizontalGnssApplied,
            productionHorizontalGnssState,
            productionHorizontalGnssReason,
            default,
            finalSummary,
            out jsonLine);

    public static bool TryBuildWorldHorizontalGnssShadowJsonLine(
        string telemetrySessionId,
        HorizontalGnssWorldShadowSnapshot snapshot,
        HorizontalGnssWorldShadowReacquisitionSnapshot reacquisition,
        HorizontalGnssWorldShadowOutageDiagnostics diagnostics,
        HorizontalGnssSmoothingShadowSnapshot smoothedSnapshot,
        HorizontalGnssSmoothingReacquisitionSnapshot smoothedReacquisition,
        bool productionHorizontalGnssApplied,
        HorizontalGnssSmoothingState productionHorizontalGnssState,
        string productionHorizontalGnssReason,
        HorizontalGnssQualityAdmissionSnapshot qualityAdmission,
        bool finalSummary,
        out string jsonLine) =>
        TryBuildWorldHorizontalGnssShadowJsonLine(
            telemetrySessionId,
            snapshot,
            reacquisition,
            diagnostics,
            smoothedSnapshot,
            smoothedReacquisition,
            productionHorizontalGnssApplied,
            productionHorizontalGnssState,
            productionHorizontalGnssReason,
            qualityAdmission,
            default,
            finalSummary,
            out jsonLine);

    public static bool TryBuildWorldHorizontalGnssShadowJsonLine(
        string telemetrySessionId,
        HorizontalGnssWorldShadowSnapshot snapshot,
        HorizontalGnssWorldShadowReacquisitionSnapshot reacquisition,
        HorizontalGnssWorldShadowOutageDiagnostics diagnostics,
        HorizontalGnssSmoothingShadowSnapshot smoothedSnapshot,
        HorizontalGnssSmoothingReacquisitionSnapshot smoothedReacquisition,
        bool productionHorizontalGnssApplied,
        HorizontalGnssSmoothingState productionHorizontalGnssState,
        string productionHorizontalGnssReason,
        HorizontalGnssQualityAdmissionSnapshot qualityAdmission,
        VerticalAltitudeProductionSnapshot productionVerticalAltitude,
        bool finalSummary,
        out string jsonLine)
    {
        var json = new JsonLine();
        json.BeginObject();
        json.Property("recordType").String("WORLD_HORIZONTAL_GNSS_SHADOW");
        json.Property("worldHorizontalGnssShadowRecordKind").String(finalSummary ? "FINAL" : "THROTTLED");
        json.Property("telemetrySessionId").String(telemetrySessionId ?? "UNAVAILABLE");
        json.Property("clockDomain").String("ANDROID_MONOTONIC_elapsedRealtimeNanos");
        json.Property("horizontalGnssWorldShadowValid").Boolean(snapshot.valid);
        json.Property("horizontalGnssWorldShadowFailureReason").String(snapshot.failureReason ?? string.Empty);
        json.Property("diagnosticShadowActive").Boolean(diagnostics.active);
        json.Property("productionRenderSource").String(diagnostics.productionRenderSource.ToString());
        json.Property("boardHiddenAgeMs").NullableNumber(
            double.IsFinite(diagnostics.boardHiddenAgeMilliseconds) ? diagnostics.boardHiddenAgeMilliseconds : (double?)null);
        json.Property("productionRotationPropagationExpired").Boolean(diagnostics.productionRotationPropagationExpired);
        json.Property("timeInNoneMs").NullableNumber(
            double.IsFinite(diagnostics.timeInNoneMilliseconds) ? diagnostics.timeInNoneMilliseconds : (double?)null);
        json.Property("targetGnssValid").Boolean(diagnostics.targetGnssValid);
        json.Property("targetGnssAgeMs").NullableNumber(
            double.IsFinite(diagnostics.targetGnssAgeMilliseconds) ? diagnostics.targetGnssAgeMilliseconds : (double?)null);
        json.Property("candidateValidWhileProductionNone").Boolean(diagnostics.candidateValidWhileProductionNone);
        json.Property("staleTargetGapCount").Number(diagnostics.staleTargetGapCount);
        json.Property("staleTargetGapDurationMs").NullableNumber(
            double.IsFinite(diagnostics.staleTargetGapDurationMilliseconds)
                ? diagnostics.staleTargetGapDurationMilliseconds
                : (double?)null);
        json.Property("visualBaselineTimestampNs").NullableNumber(
            snapshot.visualBaselineTimestampNs > 0L ? snapshot.visualBaselineTimestampNs : (long?)null);
        json.Property("gnssBaselineTimestampNs").NullableNumber(
            snapshot.gnssBaselineTimestampNs > 0L ? snapshot.gnssBaselineTimestampNs : (long?)null);
        json.Property("gnssBaselineAgeMs").NullableNumber(
            double.IsFinite(snapshot.gnssBaselineAgeMilliseconds) ? snapshot.gnssBaselineAgeMilliseconds : (double?)null);
        json.Property("targetTimestampNs").NullableNumber(
            snapshot.targetTimestampNs > 0L ? snapshot.targetTimestampNs : (long?)null);
        json.Property("gnssTargetTimestampNs").NullableNumber(
            snapshot.gnssTargetTimestampNs > 0L ? snapshot.gnssTargetTimestampNs : (long?)null);
        json.Property("gnssTargetAgeMs").NullableNumber(
            double.IsFinite(snapshot.gnssTargetAgeMilliseconds) ? snapshot.gnssTargetAgeMilliseconds : (double?)null);
        json.Property("northDeltaM").NullableNumber(snapshot.valid ? snapshot.navigationDelta.x : (float?)null);
        json.Property("eastDeltaM").NullableNumber(snapshot.valid ? snapshot.navigationDelta.y : (float?)null);
        json.Property("downDeltaM").NullableNumber(snapshot.valid ? snapshot.navigationDelta.z : (float?)null);
        json.Property("worldDeltaX").NullableNumber(snapshot.valid ? snapshot.worldDelta.x : (float?)null);
        json.Property("worldDeltaY").NullableNumber(snapshot.valid ? snapshot.worldDelta.y : (float?)null);
        json.Property("worldDeltaZ").NullableNumber(snapshot.valid ? snapshot.worldDelta.z : (float?)null);
        json.Property("candidateWorldPositionX").NullableNumber(snapshot.valid ? snapshot.candidateWorldFromCamera.position.x : (float?)null);
        json.Property("candidateWorldPositionY").NullableNumber(snapshot.valid ? snapshot.candidateWorldFromCamera.position.y : (float?)null);
        json.Property("candidateWorldPositionZ").NullableNumber(snapshot.valid ? snapshot.candidateWorldFromCamera.position.z : (float?)null);
        json.Property("worldFromNavigationRotationValid").Boolean(snapshot.hasWorldFromNavigationRotation);
        json.Property("cameraLeverArmApplied").Boolean(snapshot.cameraLeverArmApplied);
        json.Property("reacquisitionHorizontalErrorM").NullableNumber(
            reacquisition.hasComparison ? reacquisition.navigationHorizontalErrorMagnitudeMeters : (float?)null);
        json.Property("reacquisitionCandidateValid").Boolean(diagnostics.reacquisitionCandidateValid);
        json.Property("reacquisitionCandidateAgeMs").NullableNumber(
            double.IsFinite(diagnostics.reacquisitionCandidateAgeMilliseconds)
                ? diagnostics.reacquisitionCandidateAgeMilliseconds
                : (double?)null);
        json.Property("reacquisitionHorizontalResidualM").NullableNumber(
            diagnostics.reacquisitionCandidateValid && float.IsFinite(diagnostics.reacquisitionHorizontalResidualMeters)
                ? diagnostics.reacquisitionHorizontalResidualMeters
                : (float?)null);
        json.Property("reacquisitionWorldErrorX").NullableNumber(
            reacquisition.hasComparison ? reacquisition.worldPositionError.x : (float?)null);
        json.Property("reacquisitionWorldErrorY").NullableNumber(
            reacquisition.hasComparison ? reacquisition.worldPositionError.y : (float?)null);
        json.Property("reacquisitionWorldErrorZ").NullableNumber(
            reacquisition.hasComparison ? reacquisition.worldPositionError.z : (float?)null);
        json.Property("reacquisitionNavigationErrorNorthM").NullableNumber(
            reacquisition.hasComparison ? reacquisition.navigationHorizontalError.x : (float?)null);
        json.Property("reacquisitionNavigationErrorEastM").NullableNumber(
            reacquisition.hasComparison ? reacquisition.navigationHorizontalError.y : (float?)null);
        json.Property("smoothedGnssCandidateValid").Boolean(smoothedSnapshot.valid);
        json.Property("productionHorizontalGnssApplied").Boolean(productionHorizontalGnssApplied);
        json.Property("productionHorizontalGnssState").String(productionHorizontalGnssState.ToString());
        json.Property("productionHorizontalGnssReason").String(productionHorizontalGnssReason ?? string.Empty);
        json.Property("verticalProductionSource").String("Location3DAltitude");
        json.Property("verticalLocalizationState").String(productionVerticalAltitude.estimate.state.ToString());
        json.Property("verticalSourceFresh").Boolean(productionVerticalAltitude.sourceFresh);
        json.Property("verticalBaselineAltitude").NullableNumber(
            productionVerticalAltitude.baselineAvailable ? productionVerticalAltitude.baselineAltitude : (double?)null);
        json.Property("verticalCurrentAltitude").NullableNumber(
            productionVerticalAltitude.sourceFresh ? productionVerticalAltitude.currentAltitude : (double?)null);
        json.Property("verticalBaselineAltitudeTimestampNs").NullableNumber(
            productionVerticalAltitude.baselineAvailable ? productionVerticalAltitude.baselineAltitudeTimestampNs : (long?)null);
        json.Property("verticalAltitudeSourceTimestampNs").NullableNumber(
            productionVerticalAltitude.altitudeSourceTimestampNs > 0L ? productionVerticalAltitude.altitudeSourceTimestampNs : (long?)null);
        json.Property("verticalAltitudeAgeMs").NullableNumber(
            double.IsFinite(productionVerticalAltitude.altitudeAgeMilliseconds)
                ? productionVerticalAltitude.altitudeAgeMilliseconds
                : (double?)null);
        json.Property("rawDeltaAltitudeUp").NullableNumber(
            productionVerticalAltitude.baselineAvailable ? productionVerticalAltitude.rawDeltaAltitudeUp : (float?)null);
        json.Property("rawDeltaNavigationDown").NullableNumber(
            productionVerticalAltitude.baselineAvailable ? productionVerticalAltitude.rawDeltaNavigationDown : (float?)null);
        json.Property("smoothedDeltaNavigationDown").NullableNumber(
            productionVerticalAltitude.estimate.hasEstimate ? productionVerticalAltitude.smoothedDeltaNavigationDown : (float?)null);
        json.Property("productionWorldVerticalContributionX").NullableNumber(
            productionVerticalAltitude.estimate.hasEstimate ? productionVerticalAltitude.worldVerticalContribution.x : (float?)null);
        json.Property("productionWorldVerticalContributionY").NullableNumber(
            productionVerticalAltitude.estimate.hasEstimate ? productionVerticalAltitude.worldVerticalContribution.y : (float?)null);
        json.Property("productionWorldVerticalContributionZ").NullableNumber(
            productionVerticalAltitude.estimate.hasEstimate ? productionVerticalAltitude.worldVerticalContribution.z : (float?)null);
        json.Property("gnssSatelliteCount").NullableNumber(qualityAdmission.satelliteCount);
        json.Property("horizontalGnssQualityTrusted").Boolean(qualityAdmission.trusted);
        json.Property("horizontalGnssQualityTransition").String(qualityAdmission.transition.ToString());
        json.Property("qualityConfirmationCount").Number(qualityAdmission.confirmationCount);
        json.Property("qualityAdmissionReason").String(qualityAdmission.admissionReason ?? string.Empty);
        json.Property("smootherState").String(smoothedSnapshot.estimate.state.ToString());
        json.Property("smoothedGnssCandidateFailureReason").String(smoothedSnapshot.failureReason ?? string.Empty);
        json.Property("rawNorthM").NullableNumber(
            smoothedSnapshot.rawGnssCandidate.valid ? smoothedSnapshot.rawGnssCandidate.navigationDelta.x : (float?)null);
        json.Property("rawEastM").NullableNumber(
            smoothedSnapshot.rawGnssCandidate.valid ? smoothedSnapshot.rawGnssCandidate.navigationDelta.y : (float?)null);
        json.Property("rawGnssTargetTimestampNs").NullableNumber(
            smoothedSnapshot.estimate.hasEstimate ? smoothedSnapshot.estimate.rawGnssTargetTimestampNs : (long?)null);
        json.Property("rawGnssTargetNorthM").NullableNumber(
            smoothedSnapshot.estimate.hasEstimate ? smoothedSnapshot.estimate.rawGnssTarget.x : (float?)null);
        json.Property("rawGnssTargetEastM").NullableNumber(
            smoothedSnapshot.estimate.hasEstimate ? smoothedSnapshot.estimate.rawGnssTarget.y : (float?)null);
        json.Property("targetAccepted").Boolean(smoothedSnapshot.estimate.targetAccepted);
        json.Property("targetRejectedReason").String(smoothedSnapshot.estimate.targetRejectedReason ?? string.Empty);
        json.Property("acceptedTargetTimestampNs").NullableNumber(
            smoothedSnapshot.estimate.hasEstimate ? smoothedSnapshot.estimate.acceptedGnssTargetTimestampNs : (long?)null);
        json.Property("acceptedTargetNorthM").NullableNumber(
            smoothedSnapshot.estimate.hasEstimate ? smoothedSnapshot.estimate.acceptedGnssTarget.x : (float?)null);
        json.Property("acceptedTargetEastM").NullableNumber(
            smoothedSnapshot.estimate.hasEstimate ? smoothedSnapshot.estimate.acceptedGnssTarget.y : (float?)null);
        json.Property("distanceFromAcceptedTargetM").NullableNumber(
            smoothedSnapshot.estimate.hasEstimate ? smoothedSnapshot.estimate.distanceFromAcceptedTargetMeters : (float?)null);
        json.Property("temporalConsistencyCount").Number(smoothedSnapshot.estimate.temporalConsistencyCount);
        json.Property("smoothedNorthM").NullableNumber(
            smoothedSnapshot.estimate.hasEstimate ? smoothedSnapshot.navigationDelta.x : (float?)null);
        json.Property("smoothedEastM").NullableNumber(
            smoothedSnapshot.estimate.hasEstimate ? smoothedSnapshot.navigationDelta.y : (float?)null);
        json.Property("desiredCorrectionMagnitudeM").NullableNumber(
            smoothedSnapshot.estimate.hasEstimate ? smoothedSnapshot.estimate.desiredCorrection.magnitude : (float?)null);
        json.Property("appliedCorrectionMagnitudeM").NullableNumber(
            smoothedSnapshot.estimate.hasEstimate ? smoothedSnapshot.estimate.appliedCorrection.magnitude : (float?)null);
        json.Property("correctionSpeedMps").NullableNumber(
            smoothedSnapshot.estimate.hasEstimate ? smoothedSnapshot.estimate.correctionSpeedMetersPerSecond : (float?)null);
        json.Property("rawStepMagnitudeM").NullableNumber(
            smoothedSnapshot.estimate.hasEstimate ? smoothedSnapshot.rawCandidateStepMagnitudeMeters : (float?)null);
        json.Property("smoothedStepMagnitudeM").NullableNumber(
            smoothedSnapshot.estimate.hasEstimate ? smoothedSnapshot.estimate.appliedCorrection.magnitude : (float?)null);
        json.Property("smoothingTauSeconds").NullableNumber(
            smoothedSnapshot.estimate.hasEstimate ? smoothedSnapshot.estimate.tauSeconds : (float?)null);
        json.Property("maxCorrectionSpeedMps").NullableNumber(
            smoothedSnapshot.estimate.hasEstimate ? smoothedSnapshot.estimate.maximumCorrectionSpeedMetersPerSecond : (float?)null);
        json.Property("gnssFresh").Boolean(smoothedSnapshot.estimate.valid);
        json.Property("gnssAgeMs").NullableNumber(
            smoothedSnapshot.rawGnssCandidate.valid && double.IsFinite(smoothedSnapshot.rawGnssCandidate.gnssTargetAgeMilliseconds)
                ? smoothedSnapshot.rawGnssCandidate.gnssTargetAgeMilliseconds
                : (double?)null);
        json.Property("smoothedReacquisitionHorizontalErrorM").NullableNumber(
            smoothedReacquisition.hasComparison ? smoothedReacquisition.smoothedHorizontalResidualMeters : (float?)null);
        json.Property("smoothedRawReacquisitionHorizontalErrorM").NullableNumber(
            smoothedReacquisition.hasComparison ? smoothedReacquisition.rawGnssHorizontalResidualMeters : (float?)null);
        json.Property("smoothedHeldPositionReacquisitionHorizontalErrorM").NullableNumber(
            smoothedReacquisition.hasComparison ? smoothedReacquisition.heldPositionHorizontalResidualMeters : (float?)null);
        json.EndObject();
        jsonLine = json.ToString();
        return true;
    }

    /// <summary>
    /// Persists one completed visual outage without affecting telemetry polling
    /// or any render-pose decision. CameraFromBoard poses are explicit so a
    /// standalone DJI session never pretends to own an ARCore world origin.
    /// </summary>
    public void WriteOutageReacquisitionSummary(DjiOutageReacquisitionSummary summary)
    {
        if (_outageWriter == null || !summary.outage.IsValid || !summary.postVisualPose.IsValid)
            return;

        try
        {
            var json = new JsonLine();
            json.BeginObject();
            json.Property("status").String("COMPLETE");
            json.Property("telemetrySessionId").String(_telemetrySessionId ?? "UNAVAILABLE");
            json.Property("outageId").Number(summary.outage.outageId);
            json.Property("localizationSessionId").Number(summary.outage.localizationSessionId);
            json.Property("horizontalModeSegmentId").Number(summary.outage.horizontalModeSegmentId);
            json.Property("horizontalRenderMode").String(summary.outage.horizontalRenderMode.ToString());
            json.Property("modeChangedDuringOutage").Boolean(summary.outage.modeChangedDuringOutage);
            json.Property("reacquisitionMode").String(summary.reacquisitionMode.ToString());
            json.Property("renderPoseSourceAtHandover").String(DjiRenderPoseSource.Visual.ToString());
            json.Property("clockDomain").String("ANDROID_MONOTONIC_elapsedRealtimeNanos");
            json.Property("outageStartTimestampNs").Number(summary.outage.outageStartTimestampNs);
            json.Property("outageDurationMs").NullableNumber(summary.outageDurationMilliseconds);
            json.Property("propagationDurationMs").NullableNumber(summary.propagationDurationMilliseconds);
            WritePose(json, "preVisualCameraFromBoard", summary.outage.preVisualPose);
            WritePose(json, "postVisualCameraFromBoard", summary.postVisualPose);

            // Board XY is the physical board plane; Z is outward from print.
            json.Property("visualDisplacement").BeginObject();
            json.Property("valid").Boolean(summary.visualDisplacementValid);
            json.Property("boardXRightM").NullableNumber(summary.visualDisplacementValid ? summary.visualDeltaInBoard.x : (float?)null);
            json.Property("boardYUpM").NullableNumber(summary.visualDisplacementValid ? summary.visualDeltaInBoard.y : (float?)null);
            json.Property("boardZOutwardM").NullableNumber(summary.visualDisplacementValid ? summary.visualDeltaInBoard.z : (float?)null);
            json.Property("horizontalM").NullableNumber(summary.visualDisplacementValid
                ? new Vector2(summary.visualDeltaInBoard.x, summary.visualDeltaInBoard.y).magnitude
                : (float?)null);
            json.Property("verticalM").NullableNumber(summary.visualDisplacementValid ? Mathf.Abs(summary.visualDeltaInBoard.z) : (float?)null);
            json.Property("threeDimensionalM").NullableNumber(summary.visualDisplacementValid ? summary.visualDeltaInBoard.magnitude : (float?)null);
            json.Property("rotationDifferenceDeg").Number(summary.visualRotationDifferenceDegrees);
            json.Property("axisConvention").String("BOARD:+X_PRINT_RIGHT,+Y_PRINT_UP,+Z_OUTWARD; units=meters; transform=T_camera_board");
            json.EndObject();

            WritePrediction(json, "rotationOnly", summary.rotationOnly, summary.postVisualPose);
            WritePrediction(json, "guardedRendered", summary.guardedRendered, summary.postVisualPose);
            WritePrediction(json, "gnssShadow", summary.gnssShadow, summary.postVisualPose);
            WritePrediction(json, "hybridShadow", summary.hybridShadow, summary.postVisualPose);

            json.Property("guardedAtHandover").BeginObject();
            json.Property("state").String(summary.guardedStateAtHandover.ToString());
            json.Property("appliedCorrectionM").Number(summary.appliedCorrectionMeters);
            json.Property("candidateCorrectionM").Number(summary.candidateCorrectionMeters);
            json.Property("blendAlpha").Number(summary.blendAlpha);
            json.EndObject();
            json.EndObject();

            _outageWriter.WriteLine(json.ToString());
            _outageWriter.Flush();
            _outageEventCount++;
            Debug.Log(
                "DJI_OUTAGE_REACQUISITION_SUMMARY " +
                $"outageId={summary.outage.outageId} localizationSessionId={summary.outage.localizationSessionId} " +
                $"reacquisitionMode={summary.reacquisitionMode} outageDurationMs={summary.outageDurationMilliseconds:F1} " +
                $"visualHorizontalDisplacementM={FormatFinite(summary.visualDisplacementValid ? new Vector2(summary.visualDeltaInBoard.x, summary.visualDeltaInBoard.y).magnitude : float.NaN)} " +
                $"rotationOnlyValid={summary.rotationOnly.valid} guardedRenderedValid={summary.guardedRendered.valid} " +
                $"gnssShadowValid={summary.gnssShadow.valid} hybridShadowValid={summary.hybridShadow.valid} " +
                $"modeChangedDuringOutage={summary.outage.modeChangedDuringOutage}");
        }
        catch (Exception exception)
        {
            LogWriteFailure(exception);
        }
    }

    private static void WritePose(
        JsonLine json,
        string propertyName,
        DjiOutageVisualGroundTruthTracker.TrustedVisualPose pose)
    {
        json.Property(propertyName).BeginObject();
        json.Property("timestampNs").Number(pose.timestampNs);
        json.Property("positionX").Number(pose.cameraFromBoard.position.x);
        json.Property("positionY").Number(pose.cameraFromBoard.position.y);
        json.Property("positionZ").Number(pose.cameraFromBoard.position.z);
        json.Property("rotationX").Number(pose.cameraFromBoard.rotation.x);
        json.Property("rotationY").Number(pose.cameraFromBoard.rotation.y);
        json.Property("rotationZ").Number(pose.cameraFromBoard.rotation.z);
        json.Property("rotationW").Number(pose.cameraFromBoard.rotation.w);
        json.Property("transformConvention").String("T_camera_board");
        json.EndObject();
    }

    private static void WritePrediction(
        JsonLine json,
        string propertyName,
        DjiOutagePredictionPose prediction,
        DjiOutageVisualGroundTruthTracker.TrustedVisualPose postVisualPose)
    {
        json.Property(propertyName).BeginObject();
        json.Property("valid").Boolean(prediction.valid);
        json.Property("positionX").NullableNumber(prediction.valid ? prediction.cameraFromBoard.position.x : (float?)null);
        json.Property("positionY").NullableNumber(prediction.valid ? prediction.cameraFromBoard.position.y : (float?)null);
        json.Property("positionZ").NullableNumber(prediction.valid ? prediction.cameraFromBoard.position.z : (float?)null);
        json.Property("rotationX").NullableNumber(prediction.valid ? prediction.cameraFromBoard.rotation.x : (float?)null);
        json.Property("rotationY").NullableNumber(prediction.valid ? prediction.cameraFromBoard.rotation.y : (float?)null);
        json.Property("rotationZ").NullableNumber(prediction.valid ? prediction.cameraFromBoard.rotation.z : (float?)null);
        json.Property("rotationW").NullableNumber(prediction.valid ? prediction.cameraFromBoard.rotation.w : (float?)null);
        json.Property("horizontalErrorM").NullableNumber(prediction.valid && postVisualPose.IsValid
            ? DjiOutageVisualGroundTruthTracker.HorizontalPredictionErrorInBoard(prediction.cameraFromBoard, postVisualPose.cameraFromBoard)
            : (float?)null);
        json.Property("horizontalErrorAxisConvention").String("BOARD_XY; units=meters");
        json.EndObject();
    }

    private static string FormatFinite(float value) =>
        float.IsFinite(value) ? value.ToString("F4", CultureInfo.InvariantCulture) : "UNAVAILABLE";

    private static string FormatLatency(double? milliseconds) =>
        milliseconds.HasValue
            ? milliseconds.Value.ToString("F2", CultureInfo.InvariantCulture) + "ms"
            : "UNAVAILABLE";

    private void WriteSnapshot(long snapshotTimestampNs)
    {
        try
        {
            _telemetryProvider ??= FindFirstObjectByType<DjiCameraPoseProvider>();
            _boardVisionProvider ??= GetComponent<DjiBoardVisionProvider>();
            var snapshot = _telemetryProvider?.LatestSnapshot;
            var audit = snapshot?.positionAudit;
            var json = new JsonLine();
            json.BeginObject();
            json.Property("sampleIndex").Number(_sampleIndex);
            json.Property("telemetrySessionId").String(_telemetrySessionId ?? "UNAVAILABLE");
            json.Property("utc").String(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            json.Property("monotonicTimestampNs").Number(snapshotTimestampNs);
            json.Property("telemetryAvailable").Boolean(snapshot != null);
            json.Property("horizontalRenderMode").NullableString(
                _boardVisionProvider?.CurrentHorizontalTranslationRenderMode.ToString());
            json.Property("horizontalModeSegmentId").NullableNumber(
                _boardVisionProvider != null ? _boardVisionProvider.HorizontalModeSegmentId : (int?)null);

            WriteAircraftLocation(json, snapshotTimestampNs, audit);
            WriteHorizontalGnssStatus(json, audit, snapshotTimestampNs);
            WriteVelocity(json, snapshotTimestampNs);
            WriteAttitudes(json, snapshot);
            WriteBoardState(json);
            WriteHybridPositionShadow(json);
            WriteGuardedHorizontalRender(json);
            WritePhotoMetadataCompatibility(json, snapshotTimestampNs, snapshot, audit);
            json.EndObject();

            _writer.WriteLine(json.ToString());
            _sampleIndex++;
            if (_sampleIndex % flushEverySamples == 0)
                _writer.Flush();
        }
        catch (Exception exception)
        {
            LogWriteFailure(exception);
            StopSession();
        }
    }

    // The bridge replays bounded histories on every poll. Deduplication keeps
    // each true Android callback as one JSONL record without influencing GNSS admission.
    private void WriteNewHorizontalQualitySamples(long loggerTimestampNs)
    {
        try
        {
            _telemetryProvider ??= FindFirstObjectByType<DjiCameraPoseProvider>();
            var audit = _telemetryProvider?.LatestSnapshot?.positionAudit;
            if (audit == null)
                return;

            var wroteAny = false;
            wroteAny |= WriteHorizontalQualitySamples(
                audit.gpsInterferenceStateHistory,
                HorizontalQualitySampleKind.GpsInterferenceState,
                loggerTimestampNs);
            wroteAny |= WriteHorizontalQualitySamples(
                audit.fusionPositioningLevelHistory,
                HorizontalQualitySampleKind.FusionPositioningLevel,
                loggerTimestampNs);
            if (wroteAny)
                _writer.Flush();
        }
        catch (Exception exception)
        {
            LogWriteFailure(exception);
            StopSession();
        }
    }

    private bool WriteHorizontalQualitySamples(
        DJIPoseSnapshot.HorizontalQualitySample[] samples,
        HorizontalQualitySampleKind source,
        long loggerTimestampNs)
    {
        if (samples == null || samples.Length == 0)
            return false;

        var wroteAny = false;
        foreach (var sample in samples)
        {
            if (sample == null || !_horizontalQualitySampleDeduplicator.TryAccept(source, sample.timestampNs))
                continue;

            var record = new HorizontalQualityCallbackRecord(
                source,
                sample.timestampNs,
                sample.rawValue,
                sample.status,
                sample.error);
            _writer.WriteLine(BuildHorizontalQualitySampleJsonLine(_telemetrySessionId, loggerTimestampNs, record));
            _horizontalQualityCallbackRecordCount++;
            wroteAny = true;
        }

        return wroteAny;
    }

    private void WriteAircraftLocation(JsonLine json, long nowTimestampNs, DJIPoseSnapshot.PositionAudit audit)
    {
        var hasSample = TryGetLatest(_telemetryProvider?.AircraftPositionHistory, out var sample);
        var valid = hasSample && IsValidStatus(audit?.aircraftLocation3DStatus);
        json.Property("aircraftLocation3D").BeginObject();
        json.Property("available").Boolean(hasSample);
        json.Property("valid").Boolean(valid);
        json.Property("latitude").NullableNumber(hasSample ? sample.latitudeDegrees : (double?)null);
        json.Property("longitude").NullableNumber(hasSample ? sample.longitudeDegrees : (double?)null);
        json.Property("altitude").NullableNumber(hasSample ? sample.altitude : (double?)null);
        json.Property("sourceTimestampNs").NullableNumber(hasSample ? sample.timestampNs : (long?)null);
        json.Property("sampleAgeMs").NullableNumber(SampleAgeMilliseconds(nowTimestampNs, hasSample ? sample.timestampNs : 0L));
        json.Property("status").NullableString(audit?.aircraftLocation3DStatus);
        json.EndObject();
    }

    private void WriteHorizontalGnssStatus(JsonLine json, DJIPoseSnapshot.PositionAudit audit, long snapshotTimestampNs)
    {
        var satelliteCount = _telemetryProvider?.LatestGpsSatelliteCount;
        WriteHorizontalGnssStatus(json, audit, satelliteCount, snapshotTimestampNs);
    }

    public static string BuildHorizontalQualitySnapshotJsonLine(
        long snapshotTimestampNs,
        int? satelliteCount,
        DJIPoseSnapshot.PositionAudit audit)
    {
        var json = new JsonLine();
        json.BeginObject();
        WriteHorizontalGnssStatus(json, audit, satelliteCount, snapshotTimestampNs);
        json.EndObject();
        return json.ToString();
    }

    private static void WriteHorizontalGnssStatus(
        JsonLine json,
        DJIPoseSnapshot.PositionAudit audit,
        int? satelliteCount,
        long snapshotTimestampNs)
    {
        var hasInterference = TryGetLatest(audit?.gpsInterferenceStateHistory, out DJIPoseSnapshot.HorizontalQualitySample interference);
        var hasFusionLevel = TryGetLatest(audit?.fusionPositioningLevelHistory, out DJIPoseSnapshot.HorizontalQualitySample fusionLevel);
        json.Property("horizontalGnss").BeginObject();
        json.Property("gpsIsValid").NullableBoolean(TryParseBoolean(audit?.gpsIsValidRaw));
        json.Property("gpsIsValidStatus").NullableString(audit?.gpsIsValidStatus);
        json.Property("gpsSignalLevel").NullableString(audit?.gpsSignalLevelRaw);
        json.Property("gpsSignalLevelStatus").NullableString(audit?.gpsSignalLevelStatus);
        json.Property("satelliteCount").NullableNumber(satelliteCount);
        json.Property("satelliteCountAvailable").Boolean(satelliteCount.HasValue);
        json.Property("gpsInterferenceState").NullableString(audit?.gpsInterferenceStateRaw);
        json.Property("gpsInterferenceStatus").NullableString(audit?.gpsInterferenceStateStatus);
        json.Property("gpsInterferenceSupported").Boolean(audit?.gpsInterferenceStateSupport == true);
        json.Property("gpsInterferenceError").NullableString(audit?.gpsInterferenceStateError);
        json.Property("gpsInterferenceSourceTimestampNs").NullableNumber(hasInterference ? interference.timestampNs : (long?)null);
        json.Property("gpsInterferenceAgeMs").NullableNumber(
            SampleAgeMilliseconds(snapshotTimestampNs, hasInterference ? interference.timestampNs : 0L));
        json.Property("fusionPositioningLevel").NullableString(audit?.fusionPositioningLevelRaw);
        json.Property("fusionPositioningLevelStatus").NullableString(audit?.fusionPositioningLevelStatus);
        json.Property("fusionPositioningLevelSupported").Boolean(audit?.fusionPositioningLevelSupport == true);
        json.Property("fusionPositioningLevelError").NullableString(audit?.fusionPositioningLevelError);
        json.Property("fusionPositioningLevelSourceTimestampNs").NullableNumber(hasFusionLevel ? fusionLevel.timestampNs : (long?)null);
        json.Property("fusionPositioningLevelAgeMs").NullableNumber(
            SampleAgeMilliseconds(snapshotTimestampNs, hasFusionLevel ? fusionLevel.timestampNs : 0L));
        json.Property("horizontalPositionValid").Boolean(
            IsValidStatus(audit?.aircraftLocation3DStatus) &&
            IsValidStatus(audit?.gpsIsValidStatus) &&
            IsValidStatus(audit?.gpsSignalLevelStatus) &&
            IsValidStatus(audit?.gpsSatelliteCountStatus));
        json.EndObject();
    }

    public static string BuildHorizontalQualitySampleJsonLine(
        string telemetrySessionId,
        long loggerTimestampNs,
        HorizontalQualityCallbackRecord sample)
    {
        var json = new JsonLine();
        json.BeginObject();
        json.Property("recordType").String("HorizontalQualitySample");
        json.Property("telemetrySessionId").String(telemetrySessionId ?? "UNAVAILABLE");
        json.Property("clockDomain").String("ANDROID_MONOTONIC_elapsedRealtimeNanos");
        json.Property("loggerTimestampNs").Number(loggerTimestampNs);
        json.Property("source").String(sample.source.ToString());
        json.Property("sourceTimestampNs").Number(sample.timestampNs);
        json.Property("rawValue").NullableString(sample.rawValue);
        json.Property("status").NullableString(sample.status);
        json.Property("error").NullableString(sample.error);
        json.EndObject();
        return json.ToString();
    }

    private void WriteVelocity(JsonLine json, long nowTimestampNs)
    {
        var hasVelocity = TryGetLatest(_telemetryProvider?.TelemetryVelocityHistory, out var velocity);
        var value = hasVelocity ? velocity.navigationVelocityMetersPerSecond : Vector3.zero;
        var ageMs = SampleAgeMilliseconds(nowTimestampNs, hasVelocity ? velocity.timestampNs : 0L);
        json.Property("aircraftVelocity").BeginObject();
        json.Property("available").Boolean(hasVelocity);
        json.Property("north").NullableNumber(hasVelocity ? value.x : (float?)null);
        json.Property("east").NullableNumber(hasVelocity ? value.y : (float?)null);
        json.Property("down").NullableNumber(hasVelocity ? value.z : (float?)null);
        json.Property("speedMagnitude").NullableNumber(hasVelocity ? value.magnitude : (float?)null);
        json.Property("sourceTimestampNs").NullableNumber(hasVelocity ? velocity.timestampNs : (long?)null);
        json.Property("sampleAgeMs").NullableNumber(ageMs);
        json.Property("fresh500ms").Boolean(ageMs.HasValue && ageMs.Value <= 500d);
        json.Property("unitValidation").String("PENDING");
        json.EndObject();
    }

    private void WriteAttitudes(JsonLine json, DJIPoseSnapshot snapshot)
    {
        var aircraft = snapshot?.aircraft;
        var gimbal = snapshot?.gimbal;
        json.Property("aircraftAttitude").BeginObject();
        json.Property("available").Boolean(aircraft?.hasAttitude == true);
        json.Property("roll").NullableNumber(aircraft?.hasAttitude == true ? aircraft.roll : (double?)null);
        json.Property("pitch").NullableNumber(aircraft?.hasAttitude == true ? aircraft.pitch : (double?)null);
        json.Property("yaw").NullableNumber(aircraft?.hasAttitude == true ? aircraft.yaw : (double?)null);
        json.Property("sourceTimestampNs").NullableNumber(aircraft?.hasAttitude == true ? snapshot.aircraftAttitudeArrivalTimestampNs : (long?)null);
        json.EndObject();

        json.Property("gimbalAttitude").BeginObject();
        json.Property("available").Boolean(gimbal?.hasAttitude == true);
        json.Property("roll").NullableNumber(gimbal?.hasAttitude == true ? gimbal.roll : (double?)null);
        json.Property("pitch").NullableNumber(gimbal?.hasAttitude == true ? gimbal.pitch : (double?)null);
        json.Property("yaw").NullableNumber(gimbal?.hasAttitude == true ? gimbal.yaw : (double?)null);
        json.Property("sourceTimestampNs").NullableNumber(gimbal?.hasAttitude == true ? snapshot.gimbalAttitudeArrivalTimestampNs : (long?)null);
        json.EndObject();
    }

    private void WriteBoardState(JsonLine json)
    {
        var result = _boardVisionProvider?.LatestResult;
        var renderSource = _boardVisionProvider?.CurrentRenderPoseSource ?? DjiRenderPoseSource.None;
        var worldSnapshot = _worldLocalizationShadow?.Snapshot ?? default;
        var hasProductionWorldCamera = renderSource == DjiRenderPoseSource.TelemetryPropagated;
        var boardHiddenAgeSeconds = _boardVisionProvider?.BoardHiddenAgeSeconds ?? float.PositiveInfinity;
        var telemetryPhase = float.IsFinite(boardHiddenAgeSeconds)
            ? _boardVisionProvider?.GetTelemetryPropagationDiagnosticPhase(renderSource, boardHiddenAgeSeconds)
            : null;
        var smoother = _worldLocalizationShadow?.HorizontalGnssSmoothingShadow ?? default;
        var vertical = _worldLocalizationShadow?.ProductionVerticalAltitude ?? default;
        json.Property("board").BeginObject();
        json.Property("boardDetected").Boolean(result != null && result.markerCount > 0);
        json.Property("markerCount").NullableNumber(result?.markerCount);
        json.Property("visualPoseQuality").NullableString(_boardVisionProvider?.CurrentVisualPoseQuality.ToString());
        json.Property("renderPoseSource").NullableString(_boardVisionProvider?.CurrentRenderPoseSource.ToString());
        json.Property("productionLocalizationSource").String(renderSource.ToString());
        json.Property("boardHiddenAgeMs").NullableNumber(
            float.IsFinite(boardHiddenAgeSeconds)
                ? boardHiddenAgeSeconds * 1000f
                : (float?)null);
        json.Property("telemetryPropagationPhase").NullableString(telemetryPhase);
        json.Property("enteredTelemetryPropagated").Boolean(hasProductionWorldCamera);
        json.Property("telemetryPropagatedAgeMs").NullableNumber(
            hasProductionWorldCamera && float.IsFinite(boardHiddenAgeSeconds)
                ? boardHiddenAgeSeconds * 1000f
                : (float?)null);
        json.Property("horizontalLocalizationState").String(smoother.estimate.state.ToString());
        json.Property("horizontalGnssFresh").Boolean(smoother.estimate.valid);
        json.Property("horizontalFrozen").Boolean(
            smoother.estimate.state is HorizontalGnssSmoothingState.DegradedStaleGnss or HorizontalGnssSmoothingState.DegradedGnssQuality);
        json.Property("verticalSource").String(ResolveVerticalDiagnosticSource(renderSource, vertical));
        json.Property("verticalLocalizationState").String(vertical.estimate.state.ToString());
        json.Property("verticalSourceFresh").Boolean(hasProductionWorldCamera && vertical.sourceFresh);
        json.Property("rotationTelemetryValid").Boolean(_boardVisionProvider?.HasTelemetryOrientation == true);
        json.Property("productionWorldCameraPosition").BeginObject();
        json.Property("x").NullableNumber(hasProductionWorldCamera ? worldSnapshot.worldFromCamera.position.x : (float?)null);
        json.Property("y").NullableNumber(hasProductionWorldCamera ? worldSnapshot.worldFromCamera.position.y : (float?)null);
        json.Property("z").NullableNumber(hasProductionWorldCamera ? worldSnapshot.worldFromCamera.position.z : (float?)null);
        json.EndObject();
        json.Property("productionWorldCameraRotation").BeginObject();
        json.Property("x").NullableNumber(hasProductionWorldCamera ? worldSnapshot.worldFromCamera.rotation.x : (float?)null);
        json.Property("y").NullableNumber(hasProductionWorldCamera ? worldSnapshot.worldFromCamera.rotation.y : (float?)null);
        json.Property("z").NullableNumber(hasProductionWorldCamera ? worldSnapshot.worldFromCamera.rotation.z : (float?)null);
        json.Property("w").NullableNumber(hasProductionWorldCamera ? worldSnapshot.worldFromCamera.rotation.w : (float?)null);
        json.EndObject();
        json.Property("reprojectionRmsPx").NullableNumber(result != null ? result.reprojectionRms : (float?)null);
        json.Property("maxCornerResidualPx").NullableNumber(result != null ? result.maxResidual : (float?)null);
        json.Property("visionFrameTimestampNs").NullableNumber(result != null && result.visionFrameTimestampNs > 0L ? result.visionFrameTimestampNs : (long?)null);
        json.EndObject();
    }

    public static string ResolveVerticalDiagnosticSource(
        DjiRenderPoseSource renderSource, VerticalAltitudeProductionSnapshot vertical)
    {
        if (renderSource == DjiRenderPoseSource.Visual)
            return "AuthoritativeVisual";
        if (renderSource == DjiRenderPoseSource.TelemetryPropagated)
            return vertical.hasContribution ? "Location3DAltitude" : "VisualBaselineHeld";
        return "UNAVAILABLE";
    }

    private void WriteHybridPositionShadow(JsonLine json)
    {
        var hybrid = default(DjiBoardVisionProvider.HybridPositionShadowSnapshot);
        var available = _boardVisionProvider != null &&
                        _boardVisionProvider.TryGetLatestHybridPositionShadow(out hybrid);
        json.Property("hybridPositionShadow").BeginObject();
        json.Property("available").Boolean(available);
        json.Property("mode").NullableString(available ? hybrid.mode.ToString() : null);
        json.Property("northM").NullableNumber(available ? hybrid.navigationDisplacement.x : (float?)null);
        json.Property("eastM").NullableNumber(available ? hybrid.navigationDisplacement.y : (float?)null);
        json.Property("gnssAgeMs").NullableNumber(available && double.IsFinite(hybrid.gnssAgeMilliseconds) ? hybrid.gnssAgeMilliseconds : (double?)null);
        json.Property("velocityAgeMs").NullableNumber(available && double.IsFinite(hybrid.velocityAgeMilliseconds) ? hybrid.velocityAgeMilliseconds : (double?)null);
        json.Property("velocityCoverageValid").Boolean(available && hybrid.velocityCoverageValid);
        json.Property("reason").NullableString(available ? hybrid.reason : null);
        json.EndObject();
    }

    private void WriteGuardedHorizontalRender(JsonLine json)
    {
        var guarded = default(DjiBoardVisionProvider.GuardedHorizontalRenderSnapshot);
        var available = _boardVisionProvider != null &&
                        _boardVisionProvider.TryGetGuardedHorizontalRenderSnapshot(out guarded);
        json.Property("guardedHorizontal").BeginObject();
        json.Property("available").Boolean(available);
        json.Property("renderMode").NullableString(
            _boardVisionProvider?.CurrentHorizontalTranslationRenderMode.ToString());
        json.Property("segmentId").NullableNumber(
            _boardVisionProvider != null ? _boardVisionProvider.HorizontalModeSegmentId : (int?)null);
        json.Property("state").NullableString(available ? guarded.state.ToString() : null);
        json.Property("eligible").Boolean(available && guarded.eligible);
        json.Property("active").Boolean(available && guarded.active);
        json.Property("correctionNorthM").NullableNumber(available ? guarded.correctionInNavigation.x : (float?)null);
        json.Property("correctionEastM").NullableNumber(available ? guarded.correctionInNavigation.y : (float?)null);
        json.Property("correctionMagnitudeM").NullableNumber(available ? guarded.correctionInNavigation.magnitude : (float?)null);
        json.Property("appliedNorthM").NullableNumber(available ? guarded.appliedCorrectionInNavigation.x : (float?)null);
        json.Property("appliedEastM").NullableNumber(available ? guarded.appliedCorrectionInNavigation.y : (float?)null);
        json.Property("appliedMagnitudeM").NullableNumber(available ? guarded.appliedCorrectionInNavigation.magnitude : (float?)null);
        json.Property("blendAlpha").NullableNumber(available ? guarded.blendAlpha : (float?)null);
        json.Property("gnssAgeMs").NullableNumber(available && float.IsFinite(guarded.gnssAgeMilliseconds) ? guarded.gnssAgeMilliseconds : (float?)null);
        json.Property("propagationAgeMs").NullableNumber(available && float.IsFinite(guarded.propagationAgeMilliseconds) ? guarded.propagationAgeMilliseconds : (float?)null);
        json.Property("consistentSampleCount").NullableNumber(available ? guarded.consistentSampleCount : (int?)null);
        json.Property("directionDifferenceDeg").NullableNumber(available && float.IsFinite(guarded.directionDifferenceDegrees) ? guarded.directionDifferenceDegrees : (float?)null);
        json.Property("armingAgeMs").NullableNumber(available && float.IsFinite(guarded.armingAgeMilliseconds) ? guarded.armingAgeMilliseconds : (float?)null);
        json.Property("armingTimeoutMs").NullableNumber(available && float.IsFinite(guarded.armingTimeoutMilliseconds) ? guarded.armingTimeoutMilliseconds : (float?)null);
        json.Property("firstQualifyingSampleTimestampNs").NullableNumber(available && guarded.firstQualifyingSampleTimestampNs > 0L ? guarded.firstQualifyingSampleTimestampNs : (long?)null);
        json.Property("lastDistinctGnssTimestampNs").NullableNumber(available && guarded.lastDistinctGnssTimestampNs > 0L ? guarded.lastDistinctGnssTimestampNs : (long?)null);
        json.Property("enableThresholdM").NullableNumber(available ? guarded.enableThresholdMeters : (float?)null);
        json.Property("disableThresholdM").NullableNumber(available ? guarded.disableThresholdMeters : (float?)null);
        json.Property("reason").NullableString(available ? guarded.reason : null);
        json.EndObject();
    }

    private void WritePhotoMetadataCompatibility(
        JsonLine json,
        long nowTimestampNs,
        DJIPoseSnapshot snapshot,
        DJIPoseSnapshot.PositionAudit audit)
    {
        var hasPosition = TryGetLatest(_telemetryProvider?.AircraftPositionHistory, out var position);
        var hasVelocity = TryGetLatest(_telemetryProvider?.TelemetryVelocityHistory, out var velocity);
        var hasAltitude = TryGetLatest(audit?.altitudeHistory, out DJIPoseSnapshot.ScalarAltitudeSample altitude);
        var hasRelative = TryGetLatest(audit?.relativeToTakeoffHistory, out DJIPoseSnapshot.RelativePositionSample relative);
        json.Property("photoMetadataCompatibility").BeginObject();
        json.Property("gpsLatitude").NullableNumber(hasPosition ? position.latitudeDegrees : (double?)null);
        json.Property("gpsLongitude").NullableNumber(hasPosition ? position.longitudeDegrees : (double?)null);
        json.Property("absoluteAltitude").NullableNumber(hasAltitude ? altitude.value : (double?)null);
        json.Property("relativeAltitude").NullableNumber(hasRelative ? relative.z : (double?)null);
        json.Property("flightXSpeed").NullableNumber(hasVelocity ? velocity.navigationVelocityMetersPerSecond.x : (float?)null);
        json.Property("flightYSpeed").NullableNumber(hasVelocity ? velocity.navigationVelocityMetersPerSecond.y : (float?)null);
        json.Property("flightZSpeed").NullableNumber(hasVelocity ? velocity.navigationVelocityMetersPerSecond.z : (float?)null);
        json.Property("flightYawDegree").NullableNumber(snapshot?.aircraft?.hasAttitude == true ? snapshot.aircraft.yaw : (double?)null);
        json.Property("gimbalPitchDegree").NullableNumber(snapshot?.gimbal?.hasAttitude == true ? snapshot.gimbal.pitch : (double?)null);
        json.Property("sourceTimestampNs").Number(nowTimestampNs);
        json.EndObject();
    }

    private void LogWriteFailure(Exception exception)
    {
        if (_writeFailureLogged)
            return;

        _writeFailureLogged = true;
        Debug.LogWarning($"DJI_TELEMETRY_VALIDATION_LOG_ERROR type={exception.GetType().Name} message={exception.Message}");
    }

    private static bool IsValidStatus(string status) => status == "KEY_AVAILABLE_VALID";

    private static bool? TryParseBoolean(string raw)
    {
        if (bool.TryParse(raw, out var value))
            return value;
        return null;
    }

    private static double? SampleAgeMilliseconds(long nowTimestampNs, long sourceTimestampNs)
    {
        if (nowTimestampNs <= 0L || sourceTimestampNs <= 0L || nowTimestampNs < sourceTimestampNs)
            return null;
        return (nowTimestampNs - sourceTimestampNs) / 1_000_000.0;
    }


    private static bool TryGetLatest(IReadOnlyList<TelemetryAircraftPositionSample> samples, out TelemetryAircraftPositionSample latest)
    {
        latest = default;
        if (samples == null || samples.Count == 0)
            return false;
        latest = samples[samples.Count - 1];
        return latest.timestampNs > 0L;
    }

    private static bool TryGetLatest(IReadOnlyList<DjiCameraPoseProvider.TelemetryVelocitySample> samples, out DjiCameraPoseProvider.TelemetryVelocitySample latest)
    {
        latest = default;
        if (samples == null || samples.Count == 0)
            return false;
        latest = samples[samples.Count - 1];
        return latest.timestampNs > 0L;
    }

    private static bool TryGetLatest(IReadOnlyList<DJIPoseSnapshot.ScalarAltitudeSample> samples, out DJIPoseSnapshot.ScalarAltitudeSample latest)
    {
        latest = null;
        if (samples == null || samples.Count == 0)
            return false;
        latest = samples[samples.Count - 1];
        return latest != null && latest.timestampNs > 0L;
    }

    private static bool TryGetLatest(IReadOnlyList<DJIPoseSnapshot.RelativePositionSample> samples, out DJIPoseSnapshot.RelativePositionSample latest)
    {
        latest = null;
        if (samples == null || samples.Count == 0)
            return false;
        latest = samples[samples.Count - 1];
        return latest != null && latest.timestampNs > 0L;
    }

    private static bool TryGetLatest(IReadOnlyList<DJIPoseSnapshot.HorizontalQualitySample> samples, out DJIPoseSnapshot.HorizontalQualitySample latest)
    {
        latest = null;
        if (samples == null || samples.Count == 0)
            return false;
        latest = samples[samples.Count - 1];
        return latest != null && latest.timestampNs > 0L;
    }

    private sealed class JsonLine
    {
        private readonly StringBuilder _builder = new(1024);
        private readonly Stack<bool> _firstProperties = new();

        public JsonLine BeginObject()
        {
            _builder.Append('{');
            _firstProperties.Push(true);
            return this;
        }

        public JsonLine EndObject()
        {
            _builder.Append('}');
            _firstProperties.Pop();
            return this;
        }

        public JsonLine Property(string name)
        {
            if (!_firstProperties.Peek())
                _builder.Append(',');
            _firstProperties.Pop();
            _firstProperties.Push(false);
            AppendQuoted(name);
            _builder.Append(':');
            return this;
        }

        public JsonLine String(string value)
        {
            AppendQuoted(value);
            return this;
        }

        public JsonLine NullableString(string value)
        {
            if (value == null)
                _builder.Append("null");
            else
                AppendQuoted(value);
            return this;
        }

        public JsonLine Boolean(bool value)
        {
            _builder.Append(value ? "true" : "false");
            return this;
        }

        public JsonLine NullableBoolean(bool? value)
        {
            if (value.HasValue)
                Boolean(value.Value);
            else
                _builder.Append("null");
            return this;
        }

        public JsonLine Number(int value) => Number((long)value);
        public JsonLine Number(long value)
        {
            _builder.Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        public JsonLine Number(float value)
        {
            _builder.Append(float.IsFinite(value)
                ? value.ToString("R", CultureInfo.InvariantCulture)
                : "null");
            return this;
        }

        public JsonLine Number(double value)
        {
            _builder.Append(double.IsFinite(value)
                ? value.ToString("R", CultureInfo.InvariantCulture)
                : "null");
            return this;
        }

        public JsonLine NullableNumber(long? value)
        {
            if (value.HasValue)
                Number(value.Value);
            else
                _builder.Append("null");
            return this;
        }

        public JsonLine NullableNumber(int? value)
        {
            if (value.HasValue)
                Number(value.Value);
            else
                _builder.Append("null");
            return this;
        }

        public JsonLine NullableNumber(float? value)
        {
            AppendNullableFinite(value);
            return this;
        }

        public JsonLine NullableNumber(double? value)
        {
            AppendNullableFinite(value);
            return this;
        }

        public override string ToString() => _builder.ToString();

        private void AppendNullableFinite(float? value)
        {
            if (!value.HasValue || !float.IsFinite(value.Value))
                _builder.Append("null");
            else
                _builder.Append(value.Value.ToString("R", CultureInfo.InvariantCulture));
        }

        private void AppendNullableFinite(double? value)
        {
            if (!value.HasValue || !double.IsFinite(value.Value))
                _builder.Append("null");
            else
                _builder.Append(value.Value.ToString("R", CultureInfo.InvariantCulture));
        }

        private void AppendQuoted(string value)
        {
            _builder.Append('"');
            foreach (var character in value)
            {
                switch (character)
                {
                    case '\\': _builder.Append("\\\\"); break;
                    case '"': _builder.Append("\\\""); break;
                    case '\n': _builder.Append("\\n"); break;
                    case '\r': _builder.Append("\\r"); break;
                    case '\t': _builder.Append("\\t"); break;
                    default:
                        if (character < ' ')
                            _builder.Append($"\\u{(int)character:X4}");
                        else
                            _builder.Append(character);
                        break;
                }
            }
            _builder.Append('"');
        }
    }
}
