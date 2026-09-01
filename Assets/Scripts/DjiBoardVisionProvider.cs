using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// DJI-only visual localization against the physical ReferenceBoardDefinition.
/// It consumes a separate ImageReader CPU stream; the DJI OES render path is
/// left untouched. All matrices use the parentFromChild convention.
/// </summary>
[DisallowMultipleComponent]
public sealed class DjiBoardVisionProvider : MonoBehaviour
{
    public enum LocalizationState
    {
        WaitingForReference,
        Localizing,
        DjiWorldInitialized
    }

    [Header("Initialization Quality Gates")]
    [SerializeField, Min(1)] private int minimumVisibleMarkers = 2;
    [SerializeField, Min(4)] private int minimumCorners = 8;
    [SerializeField, Min(1)] private int consistentSamplesRequired = 5;
    [SerializeField, Min(0.1f)] private float maximumReprojectionRmsPixels = 4f;
    [SerializeField, Min(0.1f)] private float maximumResidualPixels = 8f;
    [SerializeField, Min(0.01f)] private float maximumSamplePositionDeltaMeters = 0.08f;
    [SerializeField, Range(1f, 90f)] private float maximumSampleRotationDeltaDegrees = 10f;

    [Header("Diagnostics")]
    [SerializeField] private bool diagnosticLogging = true;
    [SerializeField, Min(0.1f)] private float diagnosticLogIntervalSeconds = 1f;
    [SerializeField] private bool allowProvisionalCalibrationForTesting;

    public LocalizationState State { get; private set; } = LocalizationState.WaitingForReference;
    public bool HasVisualWorldPose { get; private set; }
    public Pose CurrentVisualWorldCameraPose { get; private set; } = Pose.identity;
    public Matrix4x4 WorldFromDjiCameraVisual => ReferenceFrameTransforms.WorldFromCamera(CurrentVisualWorldCameraPose);
    public NativeBoardResult LatestResult { get; private set; }
    public DjiCameraCalibration Calibration => _calibration;

    private DjiCameraCalibration _calibration;
    private DjiCameraPoseProvider _telemetryProvider;
    private int _lastFrameSequence = -1;
    private int _consistentSampleCount;
    private Pose _lastCandidateWorldPose;
    private bool _hasLastCandidate;
    private float _nextDiagnosticLogTime;
    private readonly List<float> _stationaryPositionDeltas = new();
    private readonly List<float> _stationaryRotationDeltas = new();
    private readonly List<float> _provisionalReprojectionRms = new();
    private readonly List<float> _provisionalMaxResiduals = new();
    private readonly List<float> _provisionalPositionDeltas = new();
    private readonly List<float> _provisionalRotationDeltas = new();
    private Pose _lastProvisionalCameraFromBoard;
    private bool _hasLastProvisionalCameraFromBoard;
    private int _provisionalCenterSamples;
    private int _provisionalEdgeSamples;
    private int _provisionalSampleCount;
    private Texture2D _debugLineTexture;

    [Serializable]
    public sealed class NativeBoardResult
    {
        public int frameWidth;
        public int frameHeight;
        public long timestampNs;
        public int frameSequence;
        public string detectorFrameFormat;
        public bool calibrationUsable;
        public int status;
        public int markerCount;
        public int cornerCount;
        public float reprojectionRms;
        public float maxResidual;
        public float[] cameraFromBoardPosition;
        public float[] cameraFromBoardRotationMatrix;
        public Marker[] markers;

        [Serializable]
        public sealed class Marker
        {
            public int id;
            public float decisionMargin;
            public float[][] detectedCorners;
            public float[][] projectedCorners;
        }
    }

    private void Awake()
    {
        _calibration = GetComponent<DjiCameraCalibration>() ?? gameObject.AddComponent<DjiCameraCalibration>();
        _calibration.SetAllowProvisionalCalibrationForTesting(allowProvisionalCalibrationForTesting);
        _telemetryProvider = FindFirstObjectByType<DjiCameraPoseProvider>();
    }

    private IEnumerator Start()
    {
        ConfigureNativeCpuVision();
        yield return StartCoroutine(StartWhenDjiVideoIsReady());
    }

    private void Update()
    {
        var json = DjiBoardVisionBridge.GetLatestResultJson();
        if (string.IsNullOrWhiteSpace(json))
            return;

        var result = JsonUtility.FromJson<NativeBoardResult>(json);
        if (result == null || result.frameSequence == _lastFrameSequence)
            return;

        _lastFrameSequence = result.frameSequence;
        LatestResult = result;
        ProcessResult(result);
    }

    private void OnDestroy()
    {
        DjiBoardVisionBridge.Stop();
        if (_debugLineTexture != null)
            Destroy(_debugLineTexture);
    }

    private void OnGUI()
    {
        var result = LatestResult;
        if (!diagnosticLogging || result == null || result.markers == null || result.frameWidth <= 0 || result.frameHeight <= 0)
            return;

        foreach (var marker in result.markers)
        {
            DrawPixelQuad(marker.detectedCorners, result, Color.yellow);
            if (result.status == 2)
                DrawPixelQuad(marker.projectedCorners, result, Color.cyan);
        }

        if (result.status != 2 || !TryGetCameraFromBoard(result, out var cameraFromBoard))
            return;

        DrawBoardAxes(cameraFromBoard, result);
    }

    private void ConfigureNativeCpuVision()
    {
        var layout = BuildNativeMarkerLayout(ReferenceBoardDefinition.Default.DjiFiducialMarkers);
        var data = _calibration.Current;
        DjiBoardVisionBridge.Configure(
            data.imageWidth,
            data.imageHeight,
            data.fx,
            data.fy,
            data.cx,
            data.cy,
            _calibration.DistortionCoefficients,
            layout,
            100);
        Debug.Log(
            $"DJI_REFERENCE_BOARD_LAYOUT widthMeters={ReferenceBoardDefinition.Default.WidthMeters:F3} heightMeters={ReferenceBoardDefinition.Default.HeightMeters:F3} " +
            $"phoneImageMeters={ReferenceBoardDefinition.Default.PhoneImageWidthMeters:F3}x{ReferenceBoardDefinition.Default.PhoneImageHeightMeters:F3} " +
            $"markerCount={ReferenceBoardDefinition.Default.DjiFiducialMarkers.Count}");
        if (data.provisional)
        {
            Debug.Log(
                $"DJI_PROVISIONAL_CALIBRATION_ACTIVE status={data.status} source={data.source} frame={data.imageWidth}x{data.imageHeight} " +
                $"worldInitializationAllowed={_calibration.CanInitializeWorld} allowProvisionalCalibrationForTesting={_calibration.AllowProvisionalCalibrationForTesting}");
        }
    }

    private IEnumerator StartWhenDjiVideoIsReady()
    {
        var timeoutAt = Time.realtimeSinceStartup + 15f;
        var background = FindFirstObjectByType<DJIGPUBackground>();
        while ((background == null || !background.IsReady) && Time.realtimeSinceStartup < timeoutAt)
        {
            background = FindFirstObjectByType<DJIGPUBackground>();
            yield return null;
        }

        if (DjiBoardVisionBridge.Start())
            Debug.Log("DJI_BOARD_CPU_VISION_STARTED source=ImageReader separateFromOes=true");
        else
            Debug.LogWarning("DJI_BOARD_CPU_VISION_START_FAILED");
    }

    private void ProcessResult(NativeBoardResult result)
    {
        var markerIds = result.markers == null
            ? string.Empty
            : string.Join(",", Array.ConvertAll(result.markers, marker => marker.id.ToString()));
        LogThrottled($"DJI_BOARD_MARKERS_VISIBLE count={result.markerCount} IDs={markerIds} frame={result.frameWidth}x{result.frameHeight} calibrated={result.calibrationUsable}");

        if (result.markerCount == 0)
        {
            if (State != LocalizationState.DjiWorldInitialized)
                State = LocalizationState.WaitingForReference;
            return;
        }

        if (!_calibration.IsRuntimeFrameGeometryCompatible(result.frameWidth, result.frameHeight, result.detectorFrameFormat) || !result.calibrationUsable)
        {
            Reject($"CALIBRATION_UNUSABLE_OR_FRAME_MISMATCH runtime={result.frameWidth}x{result.frameHeight}/{result.detectorFrameFormat} calibrated={_calibration.Current.imageWidth}x{_calibration.Current.imageHeight}/{_calibration.Current.detectorFrameFormat}");
            return;
        }

        if (!TryGetCameraFromBoard(result, out var cameraFromBoard))
        {
            Reject("NATIVE_BOARD_POSE_UNAVAILABLE");
            return;
        }

        Debug.Log($"DJI_BOARD_POSE_ESTIMATED T_CAMERA_BOARD position={cameraFromBoard.position} rotation={cameraFromBoard.rotation.eulerAngles}");
        Debug.Log($"DJI_BOARD_REPROJECTION_RMS rmsPixels={result.reprojectionRms:F3} maxResidualPixels={result.maxResidual:F3} corners={result.cornerCount}");
        Debug.Log($"DJI_BOARD_MAX_CORNER_ERROR pixels={result.maxResidual:F3}");
        RecordProvisionalDiagnostics(result, cameraFromBoard);

        if (!PersistentReferenceFrame.TryGetExisting(out var referenceFrame) || !referenceFrame.HasBoardPose)
        {
            State = LocalizationState.WaitingForReference;
            Reject("WORLD_BOARD_UNAVAILABLE");
            return;
        }

        // T_world_camera = T_world_board * T_board_camera, where
        // T_board_camera = inverse(T_camera_board).
        var worldFromCamera = ReferenceFrameTransforms.Compose(
            referenceFrame.WorldFromBoard,
            ReferenceFrameTransforms.MatrixFromPose(ReferenceFrameTransforms.Invert(cameraFromBoard)));
        var candidateWorldPose = ReferenceFrameTransforms.PoseFromMatrix(worldFromCamera);

        if (!PassesImageQuality(result))
        {
            Reject("IMAGE_QUALITY_GATE");
            return;
        }

        if (_hasLastCandidate)
        {
            var positionDelta = Vector3.Distance(candidateWorldPose.position, _lastCandidateWorldPose.position);
            var rotationDelta = Quaternion.Angle(candidateWorldPose.rotation, _lastCandidateWorldPose.rotation);
            if (positionDelta > maximumSamplePositionDeltaMeters || rotationDelta > maximumSampleRotationDeltaDegrees)
            {
                Reject($"TEMPORAL_INCONSISTENCY positionDeltaMeters={positionDelta:F3} rotationDeltaDegrees={rotationDelta:F2}");
                return;
            }
        }

        _lastCandidateWorldPose = candidateWorldPose;
        _hasLastCandidate = true;

        if (!_calibration.CanInitializeWorld)
        {
            State = LocalizationState.Localizing;
            LogThrottled("DJI_WORLD_INITIALIZATION_BLOCKED reason=PROVISIONAL_CALIBRATION_DIAGNOSTIC_ONLY");
            return;
        }

        _consistentSampleCount++;
        State = LocalizationState.Localizing;
        Debug.Log($"DJI_LOCALIZATION_SAMPLE_ACCEPTED count={_consistentSampleCount}/{consistentSamplesRequired} markerCount={result.markerCount}");

        if (_consistentSampleCount < consistentSamplesRequired)
            return;

        var previous = CurrentVisualWorldCameraPose;
        var hadPrevious = HasVisualWorldPose;
        CurrentVisualWorldCameraPose = candidateWorldPose;
        HasVisualWorldPose = true;
        State = LocalizationState.DjiWorldInitialized;
        if (hadPrevious)
        {
            _stationaryPositionDeltas.Add(Vector3.Distance(candidateWorldPose.position, previous.position));
            _stationaryRotationDeltas.Add(Quaternion.Angle(candidateWorldPose.rotation, previous.rotation));
        }

        Debug.Log("DJI_WORLD_INITIALIZED");
        Debug.Log($"DJI_WORLD_CAMERA_POSITION={CurrentVisualWorldCameraPose.position}");
        Debug.Log($"DJI_WORLD_CAMERA_ROTATION={CurrentVisualWorldCameraPose.rotation.eulerAngles}");
        LogVisualTelemetryRelativeMotion(candidateWorldPose);
        LogStationaryJitter();
    }

    private bool PassesImageQuality(NativeBoardResult result)
    {
        return result.markerCount >= minimumVisibleMarkers &&
               result.cornerCount >= minimumCorners &&
               float.IsFinite(result.reprojectionRms) && result.reprojectionRms <= maximumReprojectionRmsPixels &&
               float.IsFinite(result.maxResidual) && result.maxResidual <= maximumResidualPixels;
    }

    private bool TryGetCameraFromBoard(NativeBoardResult result, out Pose cameraFromBoard)
    {
        cameraFromBoard = Pose.identity;
        if (result.status != 2 || result.cameraFromBoardPosition?.Length != 3 || result.cameraFromBoardRotationMatrix?.Length != 9)
            return false;

        var rotation = result.cameraFromBoardRotationMatrix;
        var matrix = Matrix4x4.identity;
        matrix.m00 = rotation[0]; matrix.m01 = rotation[1]; matrix.m02 = rotation[2];
        matrix.m10 = rotation[3]; matrix.m11 = rotation[4]; matrix.m12 = rotation[5];
        matrix.m20 = rotation[6]; matrix.m21 = rotation[7]; matrix.m22 = rotation[8];
        matrix.m03 = result.cameraFromBoardPosition[0];
        matrix.m13 = result.cameraFromBoardPosition[1];
        matrix.m23 = result.cameraFromBoardPosition[2];
        cameraFromBoard = ReferenceFrameTransforms.PoseFromMatrix(matrix);
        return float.IsFinite(cameraFromBoard.position.x) && float.IsFinite(cameraFromBoard.position.y) && float.IsFinite(cameraFromBoard.position.z);
    }

    private void Reject(string reason)
    {
        if (State != LocalizationState.DjiWorldInitialized)
            State = LocalizationState.Localizing;
        _consistentSampleCount = 0;
        _hasLastCandidate = false;
        Debug.Log($"DJI_LOCALIZATION_SAMPLE_REJECTED reason={reason}");
    }

    private void LogVisualTelemetryRelativeMotion(Pose visualWorldPose)
    {
        if (_telemetryProvider == null || !_telemetryProvider.HasTelemetryOrientation)
        {
            Debug.Log("DJI_VISUAL_TELEMETRY_COMPARISON unavailable=TELEMETRY_ORIENTATION_UNAVAILABLE");
            return;
        }

        // NED and ARCore world are intentionally unaligned. Only future
        // relative-motion comparisons are meaningful until board localization
        // establishes that bridge; no telemetry pose is fused here.
        Debug.Log($"DJI_VISUAL_TELEMETRY_COMPARISON visualWorldRotation={visualWorldPose.rotation.eulerAngles} navigationCameraRotation={_telemetryProvider.CurrentNavigationCameraPose.rotation.eulerAngles} alignment=NOT_COMPUTED_NO_FUSION");
    }

    private void LogStationaryJitter()
    {
        if (_stationaryPositionDeltas.Count == 0)
            return;

        Debug.Log($"DJI_VISUAL_STATIONARY_JITTER samples={_stationaryPositionDeltas.Count} positionRmsMeters={Rms(_stationaryPositionDeltas):F4} rotationRmsDegrees={Rms(_stationaryRotationDeltas):F3}");
    }

    private void RecordProvisionalDiagnostics(NativeBoardResult result, Pose cameraFromBoard)
    {
        if (!_calibration.Current.provisional)
            return;

        _provisionalSampleCount++;
        _provisionalReprojectionRms.Add(result.reprojectionRms);
        _provisionalMaxResiduals.Add(result.maxResidual);
        if (_hasLastProvisionalCameraFromBoard)
        {
            _provisionalPositionDeltas.Add(Vector3.Distance(cameraFromBoard.position, _lastProvisionalCameraFromBoard.position));
            _provisionalRotationDeltas.Add(Quaternion.Angle(cameraFromBoard.rotation, _lastProvisionalCameraFromBoard.rotation));
        }

        _lastProvisionalCameraFromBoard = cameraFromBoard;
        _hasLastProvisionalCameraFromBoard = true;
        var normalizedCenterOffset = GetMarkerCenterOffset(result);
        if (normalizedCenterOffset <= 0.25f)
            _provisionalCenterSamples++;
        else
            _provisionalEdgeSamples++;

        if (_provisionalSampleCount % 10 != 0)
            return;

        // The operator obtains stationary jitter by holding the camera still;
        // these raw consecutive-frame deltas intentionally include motion.
        Debug.Log(
            $"DJI_PROVISIONAL_CALIBRATION_STATS samples={_provisionalSampleCount} " +
            $"meanRmsPixels={Mean(_provisionalReprojectionRms):F3} medianRmsPixels={Median(_provisionalReprojectionRms):F3} " +
            $"maxRmsPixels={Max(_provisionalReprojectionRms):F3} maxCornerResidualPixels={Max(_provisionalMaxResiduals):F3} " +
            $"centerSamples={_provisionalCenterSamples} edgeSamples={_provisionalEdgeSamples}");
        Debug.Log(
            $"DJI_BOARD_POSE_JITTER_POSITION samples={_provisionalPositionDeltas.Count} meters={RmsOrZero(_provisionalPositionDeltas):F5} " +
            $"condition=HOLD_CAMERA_STILL_FOR_STATIONARY_MEASUREMENT");
        Debug.Log(
            $"DJI_BOARD_POSE_JITTER_ROTATION samples={_provisionalRotationDeltas.Count} degrees={RmsOrZero(_provisionalRotationDeltas):F4} " +
            $"condition=HOLD_CAMERA_STILL_FOR_STATIONARY_MEASUREMENT");
    }

    private static float GetMarkerCenterOffset(NativeBoardResult result)
    {
        if (result.markers == null || result.markers.Length == 0)
            return 1f;

        var sum = Vector2.zero;
        var count = 0;
        foreach (var marker in result.markers)
        {
            if (marker.detectedCorners == null)
                continue;
            foreach (var corner in marker.detectedCorners)
            {
                if (corner?.Length < 2)
                    continue;
                sum += new Vector2(corner[0], corner[1]);
                count++;
            }
        }

        if (count == 0 || result.frameWidth <= 0 || result.frameHeight <= 0)
            return 1f;
        var normalized = new Vector2(sum.x / count / result.frameWidth - 0.5f, sum.y / count / result.frameHeight - 0.5f);
        return normalized.magnitude;
    }

    private void LogThrottled(string message)
    {
        if (!diagnosticLogging || Time.unscaledTime < _nextDiagnosticLogTime)
            return;
        _nextDiagnosticLogTime = Time.unscaledTime + diagnosticLogIntervalSeconds;
        Debug.Log(message);
    }

    private static float[] BuildNativeMarkerLayout(IReadOnlyList<ReferenceBoardDefinition.FiducialMarkerDefinition> markers)
    {
        var data = new float[markers.Count * 9];
        for (var index = 0; index < markers.Count; index++)
        {
            var marker = markers[index];
            var offset = index * 9;
            var rotation = marker.RotationInBoard;
            data[offset] = int.Parse(marker.Id);
            data[offset + 1] = marker.PhysicalSizeMeters;
            data[offset + 2] = marker.CenterInBoard.x;
            data[offset + 3] = marker.CenterInBoard.y;
            data[offset + 4] = marker.CenterInBoard.z;
            data[offset + 5] = rotation.x;
            data[offset + 6] = rotation.y;
            data[offset + 7] = rotation.z;
            data[offset + 8] = rotation.w;
        }
        return data;
    }

    private static float Rms(IReadOnlyList<float> values)
    {
        var sum = 0f;
        foreach (var value in values)
            sum += value * value;
        return Mathf.Sqrt(sum / values.Count);
    }

    private static float RmsOrZero(IReadOnlyList<float> values) => values.Count == 0 ? 0f : Rms(values);

    private static float Mean(IReadOnlyList<float> values)
    {
        if (values.Count == 0)
            return 0f;
        var sum = 0f;
        foreach (var value in values)
            sum += value;
        return sum / values.Count;
    }

    private static float Median(IReadOnlyList<float> values)
    {
        if (values.Count == 0)
            return 0f;
        var copy = new List<float>(values);
        copy.Sort();
        var middle = copy.Count / 2;
        return copy.Count % 2 == 0 ? (copy[middle - 1] + copy[middle]) * 0.5f : copy[middle];
    }

    private static float Max(IReadOnlyList<float> values)
    {
        var maximum = 0f;
        foreach (var value in values)
            maximum = Mathf.Max(maximum, value);
        return maximum;
    }

    private void DrawBoardAxes(Pose cameraFromBoard, NativeBoardResult result)
    {
        const float axisLengthMeters = 0.08f;
        var origin = ProjectBoardPoint(Vector3.zero, cameraFromBoard);
        DrawPixelLine(origin, ProjectBoardPoint(Vector3.right * axisLengthMeters, cameraFromBoard), result, Color.red);
        DrawPixelLine(origin, ProjectBoardPoint(Vector3.up * axisLengthMeters, cameraFromBoard), result, Color.green);
        DrawPixelLine(origin, ProjectBoardPoint(Vector3.forward * axisLengthMeters, cameraFromBoard), result, Color.blue);
    }

    private Vector2 ProjectBoardPoint(Vector3 boardPoint, Pose cameraFromBoard)
    {
        var cameraPoint = cameraFromBoard.rotation * boardPoint + cameraFromBoard.position;
        if (cameraPoint.z <= 0.0001f)
            return new Vector2(float.NaN, float.NaN);
        var data = _calibration.Current;
        var x = cameraPoint.x / cameraPoint.z;
        var y = cameraPoint.y / cameraPoint.z;
        var r2 = x * x + y * y;
        var radial = 1f + data.k1 * r2 + data.k2 * r2 * r2 + data.k3 * r2 * r2 * r2;
        var distortedX = x * radial + 2f * data.p1 * x * y + data.p2 * (r2 + 2f * x * x);
        var distortedY = y * radial + data.p1 * (r2 + 2f * y * y) + 2f * data.p2 * x * y;
        return new Vector2(data.fx * distortedX + data.cx, data.fy * distortedY + data.cy);
    }

    private void DrawPixelQuad(float[][] corners, NativeBoardResult result, Color color)
    {
        if (corners == null || corners.Length != 4)
            return;
        for (var index = 0; index < 4; index++)
        {
            var next = (index + 1) % 4;
            if (corners[index]?.Length < 2 || corners[next]?.Length < 2)
                return;
            DrawPixelLine(new Vector2(corners[index][0], corners[index][1]), new Vector2(corners[next][0], corners[next][1]), result, color);
        }
    }

    private void DrawPixelLine(Vector2 first, Vector2 second, NativeBoardResult result, Color color)
    {
        if (!float.IsFinite(first.x) || !float.IsFinite(first.y) || !float.IsFinite(second.x) || !float.IsFinite(second.y))
            return;
        var a = new Vector2(first.x / result.frameWidth * Screen.width, first.y / result.frameHeight * Screen.height);
        var b = new Vector2(second.x / result.frameWidth * Screen.width, second.y / result.frameHeight * Screen.height);
        var length = Vector2.Distance(a, b);
        if (length < 1f)
            return;
        _debugLineTexture ??= CreateDebugLineTexture();
        var previousColor = GUI.color;
        var previousMatrix = GUI.matrix;
        GUI.color = color;
        GUIUtility.RotateAroundPivot(Vector2.SignedAngle(Vector2.right, b - a), a);
        GUI.DrawTexture(new Rect(a.x, a.y - 1.5f, length, 3f), _debugLineTexture);
        GUI.matrix = previousMatrix;
        GUI.color = previousColor;
    }

    private static Texture2D CreateDebugLineTexture()
    {
        var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        texture.SetPixel(0, 0, Color.white);
        texture.Apply();
        return texture;
    }

    /// <summary>Starts a bounded raw-luma capture from the actual detector path.</summary>
    public string RequestCalibrationCapture(int frameCount = 30)
    {
        var path = DjiBoardVisionBridge.RequestCalibrationCapture(frameCount);
        if (string.IsNullOrWhiteSpace(path))
            Debug.LogWarning("DJI_CALIBRATION_CAPTURE_REQUEST_FAILED");
        else
            Debug.Log($"DJI_CALIBRATION_CAPTURE_REQUESTED frames={frameCount} path={path}");
        return path;
    }

    public void SetAllowProvisionalCalibrationForTesting(bool allowed)
    {
        allowProvisionalCalibrationForTesting = allowed;
        _calibration.SetAllowProvisionalCalibrationForTesting(allowed);
    }
}

internal static class DjiBoardVisionBridge
{
#if UNITY_ANDROID && !UNITY_EDITOR
    private const string BridgeClass = "com.sok9hu.djibridge.DJIUnityVideoBridge";
    private const string VisionClass = "com.sok9hu.djibridge.DjiBoardVisionBridge";
#endif

    public static void Configure(int width, int height, float fx, float fy, float cx, float cy, float[] distortionCoefficients, float[] layout, int intervalMs)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        using var bridge = new AndroidJavaClass(VisionClass);
        bridge.CallStatic("configure", width, height, fx, fy, cx, cy, distortionCoefficients, layout, intervalMs);
#endif
    }

    public static bool Start()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        using var bridge = new AndroidJavaClass(BridgeClass);
        return bridge.CallStatic<bool>("startBoardVision");
#else
        return false;
#endif
    }

    public static void Stop()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        using var bridge = new AndroidJavaClass(BridgeClass);
        bridge.CallStatic("stopBoardVision");
#endif
    }

    public static string GetLatestResultJson()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        using var bridge = new AndroidJavaClass(BridgeClass);
        return bridge.CallStatic<string>("getLatestBoardVisionJson");
#else
        return string.Empty;
#endif
    }

    public static string RequestCalibrationCapture(int frameCount)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        using var bridge = new AndroidJavaClass(BridgeClass);
        return bridge.CallStatic<string>("requestBoardCalibrationCapture", frameCount);
#else
        return string.Empty;
#endif
    }
}
