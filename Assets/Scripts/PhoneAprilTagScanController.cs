using System;
using System.Collections;
using Unity.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Unity.XR.CoreUtils;

[DisallowMultipleComponent]
public sealed class PhoneAprilTagScanController : MonoBehaviour
{
    private enum PoseCandidateDiagnosticMode
    {
        Auto,
        ForceCandidate0,
        ForceCandidate1
    }

    private const string RuntimeCanvasName = "Phone AprilTag Scan Canvas";
    private const string DroneViewSceneName = "DroneView";
    private const string PreviewCubeName = "Phone AprilTag Preview Cube";
    private const string ReferenceImageResourceName = "AprilTagReference";
    private const float ReferenceImageWidthMeters = 0.2f;
    private const float PrintedTagSizeMeters = 0.2f;
    private const float PreviewCubeSizeMeters = 0.14f;
    private static readonly Color PreviewCubeColor = new(1f, 0.78f, 0.05f, 1f);
    private static readonly Color[] PreviewEdgeColors =
    {
        new(0.95f, 0.15f, 0.15f, 1f), new(1f, 0.47f, 0.1f, 1f),
        new(1f, 0.92f, 0.05f, 1f), new(0.35f, 0.9f, 0.1f, 1f),
        new(0.05f, 0.85f, 0.65f, 1f), new(0.05f, 0.55f, 1f, 1f),
        new(0.2f, 0.25f, 1f, 1f), new(0.55f, 0.15f, 1f, 1f),
        new(0.88f, 0.1f, 0.9f, 1f), new(1f, 0.2f, 0.58f, 1f),
        new(0.72f, 0.38f, 0.12f, 1f), new(0.9f, 0.9f, 0.9f, 1f)
    };
    private const float PreviewEdgeThickness = 0.055f;
    private const float PreviewEdgeLength = 1.04f;
    private const int MaxDetectionImageDimension = 960;
    private const float MarkerLostTimeoutSeconds = 0.2f;
    private const int PoseCandidateStride = 13;
    private const int MaximumPoseCandidates = 2;
    private const int PoseSwitchConfirmationFrames = 4;
    private const float MaximumContinuousPositionDeltaMeters = 0.08f;
    private const float MaximumContinuousRotationDeltaDegrees = 35f;
    private const float PendingPosePositionToleranceMeters = 0.025f;
    private const float PendingPoseRotationToleranceDegrees = 12f;
    private const float MaximumAcceptedReprojectionRmsPixels = 12f;
    private const float PositionFilterTimeConstantSeconds = 0.12f;
    private const float RotationFilterTimeConstantSeconds = 0.16f;
    private const float MinimumFilterConfidence = 0.18f;
    private const float DiagnosticLogIntervalSeconds = 0.5f;
    private const float StationaryCameraAngularSpeedDegreesPerSecond = 2f;

    [SerializeField] private ARCameraManager cameraManager;
    [SerializeField] [Min(0.01f)] private float detectionIntervalSeconds = 0.03f;
    [SerializeField] [Min(1)] private int confirmationsRequired = 3;
    [SerializeField] private int targetTagId;
    [SerializeField] private PoseCandidateDiagnosticMode poseCandidateDiagnosticMode;

    private readonly float[] _nativeDetection = new float[12];
    private readonly float[] _nativePoseCandidates = new float[PoseCandidateStride * MaximumPoseCandidates];
    private readonly float[] _nativeOfficialPoseCandidates = new float[PoseCandidateStride * MaximumPoseCandidates];
    private Text _statusLabel;
    private Button _connectDroneButton;
    private Text _candidateDiagnosticLabel;
    private GameObject _previewCube;
    private ARTrackedImageManager _trackedImageManager;
    private Texture2D _runtimeReferenceTexture;
    private int _consecutiveMatches;
    private bool _markerConfirmed;
    private bool _hasTagPose;
    private float _lastPoseUpdateTime = float.NegativeInfinity;
    private XRCameraIntrinsics _cachedIntrinsics;
    private bool _hasCachedIntrinsics;
    private Vector3 _trackedTagWorldPosition;
    private Quaternion _trackedTagWorldRotation;
    private Vector3 _selectedCubeWorldPosition;
    private Quaternion _selectedCubeWorldRotation;
    private bool _hasTrackedTagWorldPose;
    private Vector3 _pendingTagWorldPosition;
    private Quaternion _pendingTagWorldRotation;
    private int _pendingPoseFrames;
    private float _lastPoseFilterTime = float.NegativeInfinity;
    private bool _hasReceivedCameraFrame;
    private float _cameraStartupTime;
    private float _lastCpuImageScanTime = float.NegativeInfinity;
    private float _lastCpuImageDiagnosticLogTime = float.NegativeInfinity;
    private float _lastWorldNormalDiagnosticLogTime = float.NegativeInfinity;
    private float _lastCandidateDiagnosticLogTime = float.NegativeInfinity;
    private float _lastRawEstimatorComparisonLogTime = float.NegativeInfinity;
    private bool _hasWorldNormalReference;
    private Vector3 _worldNormalReference;
    private readonly bool[] _hasCandidateNormalBaselines = new bool[MaximumPoseCandidates];
    private readonly Vector3[] _candidateProductionNormalBaselines = new Vector3[MaximumPoseCandidates];
    private readonly Vector3[] _candidateRawNormalBaselines = new Vector3[MaximumPoseCandidates];
    private readonly Vector3[] _candidateYFlipNormalBaselines = new Vector3[MaximumPoseCandidates];
    private readonly bool[] _candidateScoreAvailable = new bool[MaximumPoseCandidates];
    private readonly float[] _candidateRmsScores = new float[MaximumPoseCandidates];
    private readonly float[] _candidateTotalScores = new float[MaximumPoseCandidates];
    private PoseCandidateDiagnosticMode _lastDiagnosticCandidateMode;
    private bool _hasDiagnosticCameraPose;
    private Pose _lastDiagnosticCameraPose;
    private float _lastDiagnosticCameraPoseTime;
    private bool _hasRawEstimatorDiagnosticCameraPose;
    private Pose _lastRawEstimatorDiagnosticCameraPose;
    private float _lastRawEstimatorDiagnosticCameraPoseTime;
    private bool _hasOpenCvRawWorldNormalReference;
    private Vector3 _openCvRawWorldNormalReference;
    private bool _hasOfficialRawWorldNormalReference;
    private Vector3 _officialRawWorldNormalReference;

    private readonly struct CameraFrameContext
    {
        public CameraFrameContext(Pose cameraPose, long? timestampNs, Matrix4x4? displayMatrix)
        {
            this.cameraPose = cameraPose;
            this.timestampNs = timestampNs;
            this.displayMatrix = displayMatrix;
        }

        public Pose cameraPose { get; }
        public long? timestampNs { get; }
        public Matrix4x4? displayMatrix { get; }
    }

    private void Awake()
    {
        DisableLegacyPlacementPrototype();
        EnsurePhoneArCameraIsEnabled();
        CreateUi();
        AprilTagScanSession.Clear();
    }

    private void OnEnable()
    {
        DJIAprilTagNative.SetTargetTagId(targetTagId);
        EnsurePhoneArCameraIsEnabled();
        _hasReceivedCameraFrame = false;
        _cameraStartupTime = Time.unscaledTime;
        _lastDiagnosticCandidateMode = poseCandidateDiagnosticMode;
        ResetNormalDiagnosticBaselines();
        if (cameraManager != null)
            cameraManager.frameReceived += OnCameraFrameReceived;
        StartCoroutine(ScanLoop());
    }

    private void OnDisable()
    {
        StopAllCoroutines();
        if (cameraManager != null)
            cameraManager.frameReceived -= OnCameraFrameReceived;
        if (_trackedImageManager != null)
            _trackedImageManager.trackablesChanged.RemoveListener(OnTrackedImagesChanged);
        DJIAprilTagNative.ReleaseDetector();
    }

    private void OnCameraFrameReceived(ARCameraFrameEventArgs frameArgs)
    {
        _hasReceivedCameraFrame = true;
        if (cameraManager != null && cameraManager.TryGetIntrinsics(out var intrinsics) &&
            intrinsics.resolution.x > 0 && intrinsics.resolution.y > 0 &&
            intrinsics.focalLength.x > 0f && intrinsics.focalLength.y > 0f)
        {
            _cachedIntrinsics = intrinsics;
            _hasCachedIntrinsics = true;
        }

        var targetCamera = cameraManager != null ? cameraManager.GetComponent<Camera>() : Camera.main;
        if (targetCamera == null)
            return;

        // The CPU image is acquired below from this exact AR camera callback.
        // Use the matching callback pose directly instead of comparing timestamps
        // from potentially different provider clocks.
        var frameContext = new CameraFrameContext(
            new Pose(targetCamera.transform.position, targetCamera.transform.rotation),
            frameArgs.timestampNs,
            frameArgs.displayMatrix);

#if UNITY_ANDROID && !UNITY_EDITOR
        if (Time.unscaledTime - _lastCpuImageScanTime < detectionIntervalSeconds)
            return;

        _lastCpuImageScanTime = Time.unscaledTime;
        if (cameraManager == null || !cameraManager.TryAcquireLatestCpuImage(out var image))
        {
            SetStatus(BuildCameraStartupStatus());
            return;
        }

        try
        {
            ProcessCpuImage(image, frameContext);
        }
        finally
        {
            image.Dispose();
        }
#endif
    }

    private void Update()
    {
        // A stale pose would make the cube appear attached even after the marker leaves the frame.
        if (_previewCube != null && _previewCube.activeSelf &&
            Time.unscaledTime - _lastPoseUpdateTime > MarkerLostTimeoutSeconds)
        {
            _previewCube.SetActive(false);
            _hasTrackedTagWorldPose = false;
            _lastPoseFilterTime = float.NegativeInfinity;
            ResetNormalDiagnosticBaselines();
            _hasDiagnosticCameraPose = false;
        }
    }

    private IEnumerator InitializeImageTracking()
    {
#if !UNITY_ANDROID || UNITY_EDITOR
        SetStatus("Az ARCore image tracking az Android buildben működik.");
        yield break;
#else
        SetStatus("AprilTag image tracker indítása...");
        while (ARSession.state == ARSessionState.None || ARSession.state == ARSessionState.CheckingAvailability)
            yield return null;

        if (ARSession.state < ARSessionState.Ready)
        {
            SetStatus("Az ARCore image tracking nem érhető el ezen a készüléken.");
            yield break;
        }

        var origin = FindAnyObjectByType<XROrigin>();
        if (origin == null)
        {
            SetStatus("Az XR Origin nem érhető el.");
            yield break;
        }

        _trackedImageManager = origin.GetComponent<ARTrackedImageManager>();
        if (_trackedImageManager == null)
        {
            _trackedImageManager = origin.gameObject.AddComponent<ARTrackedImageManager>();
            _trackedImageManager.enabled = false;
        }

        var imageBytes = Resources.Load<TextAsset>(ReferenceImageResourceName);
        if (imageBytes == null)
        {
            SetStatus("Az AprilTag referencia kép hiányzik a buildből.");
            yield break;
        }

        _runtimeReferenceTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
        if (!_runtimeReferenceTexture.LoadImage(imageBytes.bytes, false))
        {
            SetStatus("Az AprilTag referencia kép nem tölthető be.");
            yield break;
        }

        RuntimeReferenceImageLibrary runtimeLibrary;
        try
        {
            runtimeLibrary = _trackedImageManager.CreateRuntimeLibrary();
        }
        catch (System.Exception exception)
        {
            SetStatus($"Az ARCore image tracker nem indítható: {exception.Message}");
            yield break;
        }

        if (runtimeLibrary is not MutableRuntimeReferenceImageLibrary mutableLibrary)
        {
            SetStatus("Ez az ARCore eszköz nem támogatja a runtime marker library-t.");
            yield break;
        }

        var addImage = mutableLibrary.ScheduleAddImageWithValidationJob(
            _runtimeReferenceTexture,
            "AprilTag-A4-0",
            ReferenceImageWidthMeters);
        yield return new WaitUntil(() => addImage.status.IsComplete());
        if (!addImage.status.IsSuccess())
        {
            SetStatus($"Az AprilTag referencia kép elutasítva: {addImage.status}.");
            yield break;
        }

        _trackedImageManager.referenceLibrary = runtimeLibrary;
        _trackedImageManager.requestedMaxNumberOfMovingImages = 1;
        _trackedImageManager.trackablesChanged.AddListener(OnTrackedImagesChanged);
        _trackedImageManager.enabled = true;
        SetStatus("Irányítsa a telefon kameráját az A4 AprilTag markerre.");
#endif
    }

    private void OnTrackedImagesChanged(ARTrackablesChangedEventArgs<ARTrackedImage> changes)
    {
        foreach (var trackedImage in changes.added)
            UpdateTrackedImagePreview(trackedImage);

        foreach (var trackedImage in changes.updated)
            UpdateTrackedImagePreview(trackedImage);
    }

    private void UpdateTrackedImagePreview(ARTrackedImage trackedImage)
    {
        if (trackedImage.trackingState != TrackingState.Tracking)
        {
            if (_previewCube != null)
                _previewCube.SetActive(false);
            return;
        }

        if (_previewCube == null)
        {
            _previewCube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _previewCube.name = PreviewCubeName;

            var collider = _previewCube.GetComponent<Collider>();
            if (collider != null)
                Destroy(collider);

            var cubeRenderer = _previewCube.GetComponent<Renderer>();
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Universal Render Pipeline/Lit");
            if (cubeRenderer != null && shader != null)
            {
                cubeRenderer.material = new Material(shader);
                cubeRenderer.material.color = PreviewCubeColor;
                cubeRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                cubeRenderer.receiveShadows = false;
            }
        }

        _previewCube.transform.SetParent(trackedImage.transform, false);
        _previewCube.transform.localPosition = Vector3.forward * (PreviewCubeSizeMeters * 0.5f);
        _previewCube.transform.localRotation = Quaternion.identity;
        _previewCube.transform.localScale = Vector3.one * PreviewCubeSizeMeters;
        _previewCube.SetActive(true);

        if (!_markerConfirmed)
        {
            _markerConfirmed = true;
            AprilTagScanSession.Confirm(targetTagId);
            SetStatus("AprilTag rögzítve ARCore image trackinggel. Csatlakoztassa a drónt, majd folytassa.");
            _connectDroneButton.gameObject.SetActive(true);
        }
    }

    private IEnumerator ScanLoop()
    {
#if !UNITY_ANDROID || UNITY_EDITOR
        SetStatus("Az AprilTag beolvasás az Android buildben működik.");
        yield break;
#else
        while (true)
        {
            if (cameraManager == null)
                SetStatus("A telefon kamerája nem érhető el.");
            else if (!_hasReceivedCameraFrame)
                SetStatus(BuildCameraStartupStatus());
            yield return new WaitForSecondsRealtime(0.2f);
        }
#endif
    }

    private void ProcessCpuImage(XRCpuImage image, CameraFrameContext frameContext)
    {
        // ARCore intrinsics describe this unrotated CPU image, not the display texture.
        var conversion = new XRCpuImage.ConversionParams(image, TextureFormat.RGBA32)
        {
            transformation = XRCpuImage.Transformation.None
        };
        var largestImageDimension = Mathf.Max(image.width, image.height);
        if (largestImageDimension > MaxDetectionImageDimension)
        {
            var scale = MaxDetectionImageDimension / (float)largestImageDimension;
            conversion.outputDimensions = new Vector2Int(
                Mathf.RoundToInt(image.width * scale),
                Mathf.RoundToInt(image.height * scale));
        }

        var pixels = new NativeArray<byte>(image.GetConvertedDataSize(conversion), Allocator.Temp);
        bool tagDetected;
        bool hasExactCalibration;
        try
        {
            image.Convert(conversion, pixels);
            var rgbaBytes = pixels.ToArray();
            hasExactCalibration = TryGetCameraCalibration(conversion.outputDimensions, out var fx, out var fy, out var cx, out var cy);
            if (hasExactCalibration)
            {
                var poseCandidateCount = DJIAprilTagNative.TryDetectPoseCandidatesWithOfficialDiagnostics(
                    rgbaBytes,
                    conversion.outputDimensions.x,
                    conversion.outputDimensions.y,
                    fx,
                    fy,
                    cx,
                    cy,
                    PrintedTagSizeMeters,
                    _nativeDetection,
                    _nativePoseCandidates,
                    _nativeOfficialPoseCandidates);
                if (Mathf.RoundToInt(_nativeDetection[0]) == targetTagId)
                {
                    LogCpuImageDiagnostics(image, conversion, fx, fy, cx, cy, poseCandidateCount, frameContext);
                    LogRawEstimatorComparison(frameContext);
                }
                _hasTagPose = TrySelectWorldPose(poseCandidateCount, frameContext, image.timestamp);
                tagDetected = _hasTagPose || Mathf.RoundToInt(_nativeDetection[0]) == targetTagId;
            }
            else
            {
                tagDetected = DJIAprilTagNative.TryDetect(rgbaBytes, conversion.outputDimensions.x, conversion.outputDimensions.y, _nativeDetection);
                _hasTagPose = false;
            }
        }
        finally
        {
            pixels.Dispose();
        }

        if (tagDetected && _hasTagPose)
        {
            if (!_markerConfirmed)
            {
                _consecutiveMatches++;
                SetStatus($"AprilTag {targetTagId} felismerve ({_consecutiveMatches}/{confirmationsRequired})");
                if (_consecutiveMatches >= confirmationsRequired)
                    ConfirmMarker();
            }

            if (_markerConfirmed)
                ShowMarkerPreview();
        }
        else if (tagDetected)
        {
            _consecutiveMatches = 0;
            SetStatus(hasExactCalibration
                ? "AprilTag felismerve, de a validált PnP pózt elutasította. Tartsa a teljes markert jól megvilágítva a képben."
                : "AprilTag felismerve. Várakozás az ARCore pontos kamera-kalibrációjára.");
        }
        else if (_markerConfirmed)
        {
            SetStatus("Tartsa az AprilTag-et a telefon kameraképében.");
        }
        else
        {
            _consecutiveMatches = 0;
            SetStatus($"Irányítsa a telefon kameráját az AprilTag {targetTagId} markerre.");
        }
    }

    private bool TryGetCameraCalibration(Vector2Int imageSize, out float fx, out float fy, out float cx, out float cy)
    {
        if (!_hasCachedIntrinsics || _cachedIntrinsics.resolution.x <= 0 || _cachedIntrinsics.resolution.y <= 0)
        {
            fx = fy = cx = cy = 0f;
            return false;
        }

        var intrinsicsAspect = _cachedIntrinsics.resolution.x / (float)_cachedIntrinsics.resolution.y;
        var imageAspect = imageSize.x / (float)imageSize.y;
        if (Mathf.Abs(intrinsicsAspect - imageAspect) > 0.01f)
        {
            fx = fy = cx = cy = 0f;
            return false;
        }

        var imageToIntrinsicsScale = new Vector2(
            imageSize.x / (float)_cachedIntrinsics.resolution.x,
            imageSize.y / (float)_cachedIntrinsics.resolution.y);
        fx = _cachedIntrinsics.focalLength.x * imageToIntrinsicsScale.x;
        fy = _cachedIntrinsics.focalLength.y * imageToIntrinsicsScale.y;
        cx = _cachedIntrinsics.principalPoint.x * imageToIntrinsicsScale.x;
        cy = _cachedIntrinsics.principalPoint.y * imageToIntrinsicsScale.y;
        return fx > 0f && fy > 0f;
    }

    private void LogCpuImageDiagnostics(
        XRCpuImage image,
        XRCpuImage.ConversionParams conversion,
        float fx,
        float fy,
        float cx,
        float cy,
        int poseCandidateCount,
        CameraFrameContext frameContext)
    {
        if (Time.unscaledTime - _lastCpuImageDiagnosticLogTime < DiagnosticLogIntervalSeconds)
            return;

        _lastCpuImageDiagnosticLogTime = Time.unscaledTime;
        var frameTimestampSeconds = frameContext.timestampNs.HasValue ? frameContext.timestampNs.Value / 1_000_000_000.0 : double.NaN;
        var timestampDeltaMilliseconds = frameContext.timestampNs.HasValue
            ? Math.Abs(image.timestamp - frameTimestampSeconds) * 1000.0
            : double.NaN;

        // The native detector receives precisely this unrotated converted buffer.
        Debug.Log(
            $"[DJIAprilTag] CPU/PnP input source={image.width}x{image.height} " +
            $"inputRect={conversion.inputRect} converted={conversion.outputDimensions.x}x{conversion.outputDimensions.y} " +
            $"format=RGBA32 transform={conversion.transformation} (no rotation API; None means no crop, mirror, or resize unless dimensions differ) " +
            $"ARCoreIntrinsics={_cachedIntrinsics.resolution.x}x{_cachedIntrinsics.resolution.y} " +
            $"rawFxFy=({_cachedIntrinsics.focalLength.x:F3},{_cachedIntrinsics.focalLength.y:F3}) " +
            $"rawCxCy=({_cachedIntrinsics.principalPoint.x:F3},{_cachedIntrinsics.principalPoint.y:F3}) " +
            $"passedFxFyCxCy=({fx:F3},{fy:F3},{cx:F3},{cy:F3}) " +
            $"IPPECandidates={poseCandidateCount} cpuTimestamp={image.timestamp:F6}s frameTimestamp={frameTimestampSeconds:F6}s delta={timestampDeltaMilliseconds:F3}ms " +
            $"screen={Screen.orientation}/{Screen.width}x{Screen.height} device={Input.deviceOrientation} " +
            $"displayUv={DescribeDisplayUvTransform(frameContext.displayMatrix)} " +
            $"OpenCvToUnityCameraBasis=(x,y,z)->(x,-y,z); no display-axis rotation is applied to PnP.");
    }

    private string BuildCameraStartupStatus()
    {
        if (ARSession.state == ARSessionState.None || ARSession.state == ARSessionState.CheckingAvailability)
            return "ARCore elérhetőség ellenőrzése...";

        if (ARSession.state == ARSessionState.NeedsInstall || ARSession.state == ARSessionState.Installing)
            return "Az ARCore telepítése vagy frissítése folyamatban van...";

        if (ARSession.state == ARSessionState.Unsupported)
            return "Az ARCore nem támogatott ezen a készüléken.";

        if (!_hasReceivedCameraFrame)
        {
            var elapsedSeconds = Time.unscaledTime - _cameraStartupTime;
            return elapsedSeconds < 3f
                ? "Telefonkamera indítása..."
                : "Az ARCore még nem adott kameraképet. Ellenőrizze a kameraengedélyt, majd indítsa újra az appot.";
        }

        return "Az ARCore ad kameraframe-et, de a CPU-kép még nem érhető el. Tartsa nyitva az appot pár másodpercig.";
    }

    private void ConfirmMarker()
    {
        _markerConfirmed = true;
        AprilTagScanSession.Confirm(targetTagId);
        ShowMarkerPreview();
        SetStatus("Marker rögzítve. Csatlakoztassa a drónt, majd folytassa.");
        _connectDroneButton.gameObject.SetActive(true);
    }

    private void ShowMarkerPreview()
    {
        EnsurePreviewCube();

        if (!_hasTagPose)
            return;

        _previewCube.transform.SetParent(null, true);
        _previewCube.transform.SetPositionAndRotation(_selectedCubeWorldPosition, _selectedCubeWorldRotation);
        _previewCube.transform.localScale = Vector3.one * PreviewCubeSizeMeters;
        _previewCube.SetActive(true);
        _lastPoseUpdateTime = Time.unscaledTime;
    }

    private bool TrySelectWorldPose(int poseCandidateCount, CameraFrameContext frameContext, double cpuImageTimestamp)
    {
        if (poseCandidateDiagnosticMode != _lastDiagnosticCandidateMode)
        {
            _lastDiagnosticCandidateMode = poseCandidateDiagnosticMode;
            _hasTrackedTagWorldPose = false;
            _pendingPoseFrames = 0;
            _lastPoseFilterTime = float.NegativeInfinity;
            ResetNormalDiagnosticBaselines();
            Debug.Log($"[DJIAprilTag] Candidate diagnostic mode changed to {poseCandidateDiagnosticMode}; cleared pose continuity and normal baselines.");
        }

        var cameraPose = frameContext.cameraPose;
        var forcedCandidateIndex = GetForcedCandidateIndex();
        var logCandidateDiagnostics = Time.unscaledTime - _lastCandidateDiagnosticLogTime >= DiagnosticLogIntervalSeconds;
        if (logCandidateDiagnostics)
            _lastCandidateDiagnosticLogTime = Time.unscaledTime;
        Array.Clear(_candidateScoreAvailable, 0, _candidateScoreAvailable.Length);
        var bestScore = float.PositiveInfinity;
        var bestReprojectionError = float.PositiveInfinity;
        var hasBestCandidate = false;
        var bestTagPosition = Vector3.zero;
        var bestCubeRotation = Quaternion.identity;
        var bestWorldNormal = Vector3.zero;
        var bestCandidateIndex = -1;
        var candidateLimit = Mathf.Min(poseCandidateCount, MaximumPoseCandidates);

        for (var candidateIndex = 0; candidateIndex < candidateLimit; ++candidateIndex)
        {
            var offset = candidateIndex * PoseCandidateStride;
            var reprojectionError = _nativePoseCandidates[offset + 12];
            if (float.IsNaN(reprojectionError) || float.IsInfinity(reprojectionError) ||
                reprojectionError > MaximumAcceptedReprojectionRmsPixels)
                continue;

            if (!TryConvertNativePose(offset, out var tagPosition, out var cubeCameraRotation, out _))
                continue;

            var tagWorldPosition = cameraPose.position + cameraPose.rotation * tagPosition;
            var cubeWorldRotation = cameraPose.rotation * cubeCameraRotation;
            var worldNormal = cameraPose.rotation * (cubeCameraRotation * Vector3.up);
            var cameraSpaceNormal = GetNativeCameraSpaceNormal(offset);
            var worldNormalRaw = cameraPose.rotation * cameraSpaceNormal;
            var worldNormalYFlip = cameraPose.rotation * new Vector3(cameraSpaceNormal.x, -cameraSpaceNormal.y, cameraSpaceNormal.z);

            // A fixed marker should retain the same ARCore world pose between frames.
            var reprojectionScore = reprojectionError * 0.02f;
            var positionContinuityScore = 0f;
            var rotationContinuityScore = 0f;
            if (_hasTrackedTagWorldPose)
            {
                positionContinuityScore = Vector3.Distance(tagWorldPosition, _trackedTagWorldPosition) / 0.04f;
                rotationContinuityScore = Quaternion.Angle(cubeWorldRotation, _trackedTagWorldRotation) / 15f;
            }
            var score = reprojectionScore + positionContinuityScore + rotationContinuityScore;
            _candidateScoreAvailable[candidateIndex] = true;
            _candidateRmsScores[candidateIndex] = reprojectionError;
            _candidateTotalScores[candidateIndex] = score;

            RecordCandidateNormalBaselines(candidateIndex, worldNormal, worldNormalRaw, worldNormalYFlip);
            if (logCandidateDiagnostics)
            {
                LogCandidateDiagnostics(
                    candidateIndex,
                    reprojectionError,
                    reprojectionScore,
                    positionContinuityScore,
                    rotationContinuityScore,
                    score,
                    cameraSpaceNormal,
                    worldNormal,
                    worldNormalRaw,
                    worldNormalYFlip,
                    forcedCandidateIndex);
            }

            if (forcedCandidateIndex >= 0 && candidateIndex != forcedCandidateIndex)
                continue;

            // IPPE candidates represent the same planar marker. Select by the
            // measurement quality first; continuity is used only to reject a
            // transient outlier after selection, never to keep a wrong branch.
            if (reprojectionError > bestReprojectionError ||
                (Mathf.Approximately(reprojectionError, bestReprojectionError) && score >= bestScore))
                continue;

            bestReprojectionError = reprojectionError;
            bestScore = score;
            hasBestCandidate = true;
            bestTagPosition = tagWorldPosition;
            bestCubeRotation = cubeWorldRotation;
            bestWorldNormal = worldNormal;
            bestCandidateIndex = candidateIndex;
        }

        if (!hasBestCandidate)
        {
            Debug.Log(candidateLimit == 0
                ? "[DJIAprilTag] Native PnP returned no valid candidates for this camera frame."
                : "[DJIAprilTag] Unity rejected every raw OpenCV PnP candidate before world-pose selection.");
            return false;
        }

        LogWorldNormalDiagnostics(bestCandidateIndex, bestWorldNormal, frameContext, cpuImageTimestamp);
        if (logCandidateDiagnostics)
            LogCandidateSelectionDecision(bestCandidateIndex, forcedCandidateIndex);

        if (_hasTrackedTagWorldPose && !IsContinuousPose(bestTagPosition, bestCubeRotation))
        {
            TrackPendingPose(bestTagPosition, bestCubeRotation);
            if (_pendingPoseFrames < PoseSwitchConfirmationFrames)
            {
                Debug.Log($"[DJIAprilTag] Unity pose selection candidate={bestCandidateIndex} deferred by continuity gate ({_pendingPoseFrames}/{PoseSwitchConfirmationFrames}).");
                return true;
            }
        }

        _pendingPoseFrames = 0;
        ApplyFilteredWorldPose(bestTagPosition, bestCubeRotation, reprojectionError: _candidateRmsScores[bestCandidateIndex]);
        _hasTrackedTagWorldPose = true;
        Debug.Log(
            $"[DJIAprilTag] Unity selected raw OpenCV PnP candidate={bestCandidateIndex} " +
            $"mode={poseCandidateDiagnosticMode} score={bestScore:F4} " +
            $"reason={(forcedCandidateIndex >= 0 ? "forced diagnostic selection with continuity and temporal filtering" : "lowest reprojection RMS, followed by outlier gating and temporal filtering")}.");
        return true;
    }

    private void ApplyFilteredWorldPose(Vector3 rawTagWorldPosition, Quaternion rawCubeWorldRotation, float reprojectionError)
    {
        var now = Time.unscaledTime;
        if (!_hasTrackedTagWorldPose || float.IsNegativeInfinity(_lastPoseFilterTime))
        {
            _trackedTagWorldPosition = rawTagWorldPosition;
            _trackedTagWorldRotation = rawCubeWorldRotation;
        }
        else
        {
            var elapsedSeconds = Mathf.Clamp(now - _lastPoseFilterTime, 0.001f, 0.2f);
            var confidence = Mathf.Lerp(
                MinimumFilterConfidence,
                1f,
                Mathf.InverseLerp(MaximumAcceptedReprojectionRmsPixels, 0.5f, reprojectionError));
            var positionAlpha = (1f - Mathf.Exp(-elapsedSeconds / PositionFilterTimeConstantSeconds)) * confidence;
            var rotationAlpha = (1f - Mathf.Exp(-elapsedSeconds / RotationFilterTimeConstantSeconds)) * confidence;

            _trackedTagWorldPosition = Vector3.Lerp(_trackedTagWorldPosition, rawTagWorldPosition, positionAlpha);
            _trackedTagWorldRotation = Quaternion.Slerp(_trackedTagWorldRotation, rawCubeWorldRotation, rotationAlpha);
        }

        // The cube local +Y is the marker normal. Recompute its center from the
        // filtered tag pose so the bottom face remains on the marker plane.
        _selectedCubeWorldRotation = _trackedTagWorldRotation;
        _selectedCubeWorldPosition = _trackedTagWorldPosition +
            (_selectedCubeWorldRotation * Vector3.up) * (PreviewCubeSizeMeters * 0.5f);
        _lastPoseFilterTime = now;
    }

    private void LogRawEstimatorComparison(CameraFrameContext frameContext)
    {
        var now = Time.unscaledTime;
        var cameraAngularSpeed = 0f;
        if (_hasRawEstimatorDiagnosticCameraPose)
        {
            var elapsedSeconds = now - _lastRawEstimatorDiagnosticCameraPoseTime;
            if (elapsedSeconds > Mathf.Epsilon)
            {
                cameraAngularSpeed = Quaternion.Angle(
                    _lastRawEstimatorDiagnosticCameraPose.rotation,
                    frameContext.cameraPose.rotation) / elapsedSeconds;
            }
        }

        _lastRawEstimatorDiagnosticCameraPose = frameContext.cameraPose;
        _lastRawEstimatorDiagnosticCameraPoseTime = now;
        _hasRawEstimatorDiagnosticCameraPose = true;
        if (now - _lastRawEstimatorComparisonLogTime < DiagnosticLogIntervalSeconds)
            return;

        _lastRawEstimatorComparisonLogTime = now;
        LogRawEstimatorCandidates(
            "OpenCV/IPPE",
            "reprojectionRmsPx",
            _nativePoseCandidates,
            isOfficialAprilTagPose: false,
            frameContext,
            cameraAngularSpeed,
            ref _hasOpenCvRawWorldNormalReference,
            ref _openCvRawWorldNormalReference);
        LogRawEstimatorCandidates(
            "OfficialAprilTag",
            "objectSpaceError",
            _nativeOfficialPoseCandidates,
            isOfficialAprilTagPose: true,
            frameContext,
            cameraAngularSpeed,
            ref _hasOfficialRawWorldNormalReference,
            ref _officialRawWorldNormalReference);
    }

    private void LogRawEstimatorCandidates(
        string estimatorName,
        string errorName,
        float[] poseCandidates,
        bool isOfficialAprilTagPose,
        CameraFrameContext frameContext,
        float cameraAngularSpeed,
        ref bool hasStationaryWorldNormalReference,
        ref Vector3 stationaryWorldNormalReference)
    {
        var selectedCandidateIndex = GetLowestErrorCandidateIndex(poseCandidates);
        var hasSelectedPose = TryGetRawEstimatorPose(
            poseCandidates, selectedCandidateIndex, isOfficialAprilTagPose,
            out _, out var selectedCubeCameraRotation, out _);
        var selectedWorldNormal = hasSelectedPose
            ? frameContext.cameraPose.rotation * (selectedCubeCameraRotation * Vector3.up)
            : Vector3.zero;

        // These are raw estimator diagnostics only. The production temporal
        // filter is intentionally not read or updated in this comparison path.
        if (hasSelectedPose && !hasStationaryWorldNormalReference &&
            cameraAngularSpeed <= StationaryCameraAngularSpeedDegreesPerSecond)
        {
            stationaryWorldNormalReference = selectedWorldNormal;
            hasStationaryWorldNormalReference = true;
        }

        for (var candidateIndex = 0; candidateIndex < MaximumPoseCandidates; ++candidateIndex)
        {
            if (!TryGetRawEstimatorPose(
                    poseCandidates, candidateIndex, isOfficialAprilTagPose,
                    out var cameraPosition, out var cubeCameraRotation, out _))
            {
                continue;
            }

            var offset = candidateIndex * PoseCandidateStride;
            var error = poseCandidates[offset + 12];
            var rawCameraNormal = GetCameraSpaceNormal(poseCandidates, offset);
            var worldNormal = frameContext.cameraPose.rotation * (cubeCameraRotation * Vector3.up);
            var referenceDrift = hasStationaryWorldNormalReference
                ? $"{Vector3.Angle(stationaryWorldNormalReference, worldNormal):F2}deg"
                : "baseline-unavailable";
            Debug.Log(
                $"[DJIAprilTag] Raw comparison estimator={estimatorName} candidate={candidateIndex} " +
                $"selected={(candidateIndex == selectedCandidateIndex ? "yes" : "no")} {errorName}={error:F8} " +
                $"t=({cameraPosition.x:F5},{cameraPosition.y:F5},{cameraPosition.z:F5}) " +
                $"R={FormatPoseRotationMatrix(poseCandidates, offset)} " +
                $"cameraNormal=({rawCameraNormal.x:F5},{rawCameraNormal.y:F5},{rawCameraNormal.z:F5}) " +
                $"worldNormal=({worldNormal.x:F5},{worldNormal.y:F5},{worldNormal.z:F5}) " +
                $"stationaryReferenceDrift={referenceDrift}.");
        }

        Debug.Log(
            $"[DJIAprilTag] Raw comparison estimator={estimatorName} selectedCandidate={selectedCandidateIndex} " +
            $"selectedWorldNormal={(hasSelectedPose ? selectedWorldNormal.ToString("F5") : "unavailable")} " +
            $"cameraAngularSpeed={cameraAngularSpeed:F2}deg/s " +
            $"stationaryBaseline={(hasStationaryWorldNormalReference ? "available" : "waiting for a stationary frame")}.");
    }

    private bool TryGetRawEstimatorPose(
        float[] poseCandidates,
        int candidateIndex,
        bool isOfficialAprilTagPose,
        out Vector3 tagPosition,
        out Quaternion cubeCameraRotation,
        out Vector3 outwardNormal)
    {
        tagPosition = Vector3.zero;
        cubeCameraRotation = Quaternion.identity;
        outwardNormal = Vector3.zero;
        if (candidateIndex < 0 || candidateIndex >= MaximumPoseCandidates)
            return false;

        var offset = candidateIndex * PoseCandidateStride;
        if (!HasValidPoseCandidateError(poseCandidates, offset))
            return false;

        return isOfficialAprilTagPose
            ? TryConvertOfficialAprilTagPose(poseCandidates, offset, out tagPosition, out cubeCameraRotation, out outwardNormal)
            : TryConvertNativePose(offset, out tagPosition, out cubeCameraRotation, out outwardNormal);
    }

    private static int GetLowestErrorCandidateIndex(float[] poseCandidates)
    {
        var selectedCandidateIndex = -1;
        var lowestError = float.PositiveInfinity;
        for (var candidateIndex = 0; candidateIndex < MaximumPoseCandidates; ++candidateIndex)
        {
            var offset = candidateIndex * PoseCandidateStride;
            if (!HasValidPoseCandidateError(poseCandidates, offset) || poseCandidates[offset + 12] >= lowestError)
                continue;

            lowestError = poseCandidates[offset + 12];
            selectedCandidateIndex = candidateIndex;
        }
        return selectedCandidateIndex;
    }

    private static bool HasValidPoseCandidateError(float[] poseCandidates, int offset)
    {
        if (poseCandidates == null || offset < 0 || offset + PoseCandidateStride > poseCandidates.Length)
            return false;

        var error = poseCandidates[offset + 12];
        return !float.IsNaN(error) && !float.IsInfinity(error) && error < float.MaxValue * 0.5f;
    }

    private static Vector3 GetCameraSpaceNormal(float[] poseCandidates, int offset)
    {
        return new Vector3(
            poseCandidates[offset + 5],
            poseCandidates[offset + 8],
            poseCandidates[offset + 11]).normalized;
    }

    private static string FormatPoseRotationMatrix(float[] poseCandidates, int offset)
    {
        return $"[{poseCandidates[offset + 3]:F4},{poseCandidates[offset + 4]:F4},{poseCandidates[offset + 5]:F4};" +
               $"{poseCandidates[offset + 6]:F4},{poseCandidates[offset + 7]:F4},{poseCandidates[offset + 8]:F4};" +
               $"{poseCandidates[offset + 9]:F4},{poseCandidates[offset + 10]:F4},{poseCandidates[offset + 11]:F4}]";
    }

    private static bool TryConvertOfficialAprilTagPose(
        float[] poseCandidates,
        int offset,
        out Vector3 tagPosition,
        out Quaternion cubeCameraRotation,
        out Vector3 outwardNormal)
    {
        // This is Keijiro's AprilTag-to-Unity reflection: R_unity = S * R * S
        // and t_unity = S * t, where S = diag(1, -1, 1). The final fixed
        // tag-to-cube rotation only maps the cube's bottom face onto the tag.
        tagPosition = new Vector3(
            poseCandidates[offset],
            -poseCandidates[offset + 1],
            poseCandidates[offset + 2]);
        var tagRight = new Vector3(
            poseCandidates[offset + 3],
            -poseCandidates[offset + 6],
            poseCandidates[offset + 9]);
        var tagUp = new Vector3(
            -poseCandidates[offset + 4],
            poseCandidates[offset + 7],
            -poseCandidates[offset + 10]);
        var tagForward = new Vector3(
            poseCandidates[offset + 5],
            -poseCandidates[offset + 8],
            poseCandidates[offset + 11]);

        cubeCameraRotation = Quaternion.identity;
        outwardNormal = Vector3.zero;
        if (!IsFinite(tagPosition) || !IsFinite(tagRight) || !IsFinite(tagUp) || !IsFinite(tagForward) ||
            tagRight.sqrMagnitude < 0.9f || tagUp.sqrMagnitude < 0.9f || tagForward.sqrMagnitude < 0.9f)
        {
            return false;
        }

        tagRight.Normalize();
        tagUp = Vector3.ProjectOnPlane(tagUp, tagRight).normalized;
        var measuredForward = Vector3.Cross(tagRight, tagUp).normalized;
        tagForward.Normalize();
        if (tagUp.sqrMagnitude < 0.9f || measuredForward.sqrMagnitude < 0.9f ||
            Vector3.Dot(measuredForward, tagForward) < 0.95f)
        {
            return false;
        }

        var tagCameraRotation = Quaternion.LookRotation(measuredForward, tagUp);
        cubeCameraRotation = tagCameraRotation * Quaternion.AngleAxis(-90f, Vector3.right);
        outwardNormal = cubeCameraRotation * Vector3.up;
        return IsFinite(outwardNormal);
    }

    private int GetForcedCandidateIndex()
    {
        return poseCandidateDiagnosticMode switch
        {
            PoseCandidateDiagnosticMode.ForceCandidate0 => 0,
            PoseCandidateDiagnosticMode.ForceCandidate1 => 1,
            _ => -1
        };
    }

    private Vector3 GetNativeCameraSpaceNormal(int offset)
    {
        return GetCameraSpaceNormal(_nativePoseCandidates, offset);
    }

    private void ResetNormalDiagnosticBaselines()
    {
        _hasWorldNormalReference = false;
        Array.Clear(_hasCandidateNormalBaselines, 0, _hasCandidateNormalBaselines.Length);
        _hasOpenCvRawWorldNormalReference = false;
        _hasOfficialRawWorldNormalReference = false;
        _hasRawEstimatorDiagnosticCameraPose = false;
    }

    private void RecordCandidateNormalBaselines(
        int candidateIndex,
        Vector3 productionWorldNormal,
        Vector3 rawWorldNormal,
        Vector3 yFlipWorldNormal)
    {
        if (_hasCandidateNormalBaselines[candidateIndex])
            return;

        _candidateProductionNormalBaselines[candidateIndex] = productionWorldNormal;
        _candidateRawNormalBaselines[candidateIndex] = rawWorldNormal;
        _candidateYFlipNormalBaselines[candidateIndex] = yFlipWorldNormal;
        _hasCandidateNormalBaselines[candidateIndex] = true;
    }

    private void LogCandidateDiagnostics(
        int candidateIndex,
        float reprojectionError,
        float reprojectionScore,
        float positionContinuityScore,
        float rotationContinuityScore,
        float totalScore,
        Vector3 cameraSpaceNormal,
        Vector3 productionWorldNormal,
        Vector3 rawWorldNormal,
        Vector3 yFlipWorldNormal,
        int forcedCandidateIndex)
    {
        var productionDrift = Vector3.Angle(_candidateProductionNormalBaselines[candidateIndex], productionWorldNormal);
        var rawDrift = Vector3.Angle(_candidateRawNormalBaselines[candidateIndex], rawWorldNormal);
        var yFlipDrift = Vector3.Angle(_candidateYFlipNormalBaselines[candidateIndex], yFlipWorldNormal);
        var selection = forcedCandidateIndex < 0
            ? "auto"
            : forcedCandidateIndex == candidateIndex ? "forced-selected" : "forced-skipped";
        Debug.Log(
            $"[DJIAprilTag] Candidate diagnostic index={candidateIndex} selection={selection} " +
            $"rms={reprojectionError:F4} score(reproj={reprojectionScore:F4},pos={positionContinuityScore:F4},rot={rotationContinuityScore:F4},total={totalScore:F4}) " +
            $"normalCv=({cameraSpaceNormal.x:F4},{cameraSpaceNormal.y:F4},{cameraSpaceNormal.z:F4}) " +
            $"worldNormalProduction=({productionWorldNormal.x:F4},{productionWorldNormal.y:F4},{productionWorldNormal.z:F4}) drift={productionDrift:F2}deg " +
            $"worldNormalRaw=({rawWorldNormal.x:F4},{rawWorldNormal.y:F4},{rawWorldNormal.z:F4}) drift={rawDrift:F2}deg " +
            $"worldNormalYFlip=({yFlipWorldNormal.x:F4},{yFlipWorldNormal.y:F4},{yFlipWorldNormal.z:F4}) drift={yFlipDrift:F2}deg.");
    }

    private void LogCandidateSelectionDecision(int selectedCandidateIndex, int forcedCandidateIndex)
    {
        if (forcedCandidateIndex >= 0)
        {
            Debug.Log($"[DJIAprilTag] Candidate selection explanation: candidate={selectedCandidateIndex} was selected because {poseCandidateDiagnosticMode} forces it; RMS and continuity scores were not used to choose it.");
            return;
        }

        var lowestRmsCandidateIndex = -1;
        var lowestRms = float.PositiveInfinity;
        for (var candidateIndex = 0; candidateIndex < MaximumPoseCandidates; ++candidateIndex)
        {
            if (!_candidateScoreAvailable[candidateIndex] || _candidateRmsScores[candidateIndex] >= lowestRms)
                continue;

            lowestRms = _candidateRmsScores[candidateIndex];
            lowestRmsCandidateIndex = candidateIndex;
        }

        var explanation = selectedCandidateIndex == lowestRmsCandidateIndex
            ? "it has the lowest reprojection RMS for this frame"
            : $"its total score={_candidateTotalScores[selectedCandidateIndex]:F4} is lower after position/rotation continuity terms than candidate {lowestRmsCandidateIndex}, despite that candidate having lower RMS={lowestRms:F4}";
        Debug.Log($"[DJIAprilTag] Candidate selection explanation: candidate={selectedCandidateIndex} selected because {explanation}.");
    }

    private void LogWorldNormalDiagnostics(
        int candidateIndex,
        Vector3 worldNormal,
        CameraFrameContext frameContext,
        double cpuImageTimestamp)
    {
        var now = Time.unscaledTime;
        var angularSpeedDegreesPerSecond = 0f;
        if (_hasDiagnosticCameraPose)
        {
            var elapsedSeconds = now - _lastDiagnosticCameraPoseTime;
            if (elapsedSeconds > Mathf.Epsilon)
                angularSpeedDegreesPerSecond = Quaternion.Angle(_lastDiagnosticCameraPose.rotation, frameContext.cameraPose.rotation) / elapsedSeconds;
        }

        _lastDiagnosticCameraPose = frameContext.cameraPose;
        _lastDiagnosticCameraPoseTime = now;
        _hasDiagnosticCameraPose = true;

        if (!_hasWorldNormalReference)
        {
            _worldNormalReference = worldNormal;
            _hasWorldNormalReference = true;
        }

        if (now - _lastWorldNormalDiagnosticLogTime < DiagnosticLogIntervalSeconds)
            return;

        _lastWorldNormalDiagnosticLogTime = now;
        var normalDriftDegrees = Vector3.Angle(_worldNormalReference, worldNormal);
        var frameTimestampSeconds = frameContext.timestampNs.HasValue ? frameContext.timestampNs.Value / 1_000_000_000.0 : double.NaN;
        var timestampDeltaMilliseconds = frameContext.timestampNs.HasValue
            ? Math.Abs(cpuImageTimestamp - frameTimestampSeconds) * 1000.0
            : double.NaN;
        Debug.Log(
            $"[DJIAprilTag] World-normal candidate={candidateIndex} worldNormal=({worldNormal.x:F4},{worldNormal.y:F4},{worldNormal.z:F4}) " +
            $"referenceDrift={normalDriftDegrees:F2}deg cameraAngularSpeed={angularSpeedDegreesPerSecond:F1}deg/s " +
            $"cpuToFrameDelta={timestampDeltaMilliseconds:F3}ms " +
            $"cameraWorldBasis R=({frameContext.cameraPose.rotation * Vector3.right}) " +
            $"U=({frameContext.cameraPose.rotation * Vector3.up}) F=({frameContext.cameraPose.rotation * Vector3.forward})");
    }

    private static string DescribeDisplayUvTransform(Matrix4x4? displayMatrix)
    {
        if (!displayMatrix.HasValue)
            return "unavailable";

        var matrix = displayMatrix.Value;
        var origin = TransformUv(matrix, 0f, 0f);
        var uAxis = TransformUv(matrix, 1f, 0f) - origin;
        var vAxis = TransformUv(matrix, 0f, 1f) - origin;
        return $"origin={origin} U={uAxis} V={vAxis}";
    }

    private static Vector2 TransformUv(Matrix4x4 matrix, float u, float v)
    {
        var transformed = matrix * new Vector4(u, v, 0f, 1f);
        return new Vector2(transformed.x / transformed.w, transformed.y / transformed.w);
    }

    private bool TryConvertNativePose(int offset, out Vector3 tagPosition, out Quaternion cubeCameraRotation, out Vector3 outwardNormal)
    {
        // OpenCV and AprilTag use right/down/forward. Unity camera-local space is
        // right/up/forward. The marker itself is left-handed, so build a proper
        // cube rotation from explicit right, upward and outward directions.
        tagPosition = new Vector3(
            _nativePoseCandidates[offset],
            -_nativePoseCandidates[offset + 1],
            _nativePoseCandidates[offset + 2]);
        var tagRight = new Vector3(
            _nativePoseCandidates[offset + 3],
            -_nativePoseCandidates[offset + 6],
            _nativePoseCandidates[offset + 9]);
        var tagDown = new Vector3(
            _nativePoseCandidates[offset + 4],
            -_nativePoseCandidates[offset + 7],
            _nativePoseCandidates[offset + 10]);
        var tagIntoPlane = new Vector3(
            _nativePoseCandidates[offset + 5],
            -_nativePoseCandidates[offset + 8],
            _nativePoseCandidates[offset + 11]);

        cubeCameraRotation = Quaternion.identity;
        outwardNormal = Vector3.zero;
        if (!IsFinite(tagPosition) || !IsFinite(tagRight) || !IsFinite(tagDown) || !IsFinite(tagIntoPlane) ||
            tagRight.sqrMagnitude < 0.9f || tagDown.sqrMagnitude < 0.9f || tagIntoPlane.sqrMagnitude < 0.9f)
        {
            return false;
        }

        tagRight.Normalize();
        tagDown = Vector3.ProjectOnPlane(tagDown, tagRight).normalized;
        tagIntoPlane = Vector3.ProjectOnPlane(tagIntoPlane, tagRight).normalized;
        outwardNormal = -tagIntoPlane;
        var markerUp = -tagDown;
        var measuredOutwardNormal = Vector3.Cross(tagRight, tagDown).normalized;
        if (tagDown.sqrMagnitude < 0.9f || tagIntoPlane.sqrMagnitude < 0.9f ||
            Vector3.Dot(measuredOutwardNormal, outwardNormal) < 0.95f)
        {
            return false;
        }

        // The cube's local +Y is the marker's outward normal, so its bottom face
        // remains on the marker plane. Its local +Z follows the marker's up axis.
        cubeCameraRotation = Quaternion.LookRotation(markerUp, outwardNormal);
        return IsFinite(outwardNormal);
    }

    private bool IsContinuousPose(Vector3 tagWorldPosition, Quaternion cubeWorldRotation)
    {
        return Vector3.Distance(tagWorldPosition, _trackedTagWorldPosition) <= MaximumContinuousPositionDeltaMeters &&
               Quaternion.Angle(cubeWorldRotation, _trackedTagWorldRotation) <= MaximumContinuousRotationDeltaDegrees;
    }

    private void TrackPendingPose(Vector3 tagWorldPosition, Quaternion cubeWorldRotation)
    {
        if (_pendingPoseFrames > 0 &&
            Vector3.Distance(tagWorldPosition, _pendingTagWorldPosition) <= PendingPosePositionToleranceMeters &&
            Quaternion.Angle(cubeWorldRotation, _pendingTagWorldRotation) <= PendingPoseRotationToleranceDegrees)
        {
            ++_pendingPoseFrames;
            return;
        }

        _pendingTagWorldPosition = tagWorldPosition;
        _pendingTagWorldRotation = cubeWorldRotation;
        _pendingPoseFrames = 1;
    }

    private void EnsurePreviewCube()
    {
        if (_previewCube != null)
            return;

        _previewCube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        _previewCube.name = PreviewCubeName;

        var collider = _previewCube.GetComponent<Collider>();
        if (collider != null)
            Destroy(collider);

        var cubeRenderer = _previewCube.GetComponent<Renderer>();
        var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Universal Render Pipeline/Lit");
        if (cubeRenderer != null && shader != null)
        {
            cubeRenderer.material = new Material(shader);
            cubeRenderer.material.color = PreviewCubeColor;
            cubeRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            cubeRenderer.receiveShadows = false;
            CreatePreviewEdges(_previewCube.transform, shader);
            CreateMarkerNormalDebug(_previewCube.transform, shader);
        }
    }

    private void CreateMarkerNormalDebug(Transform parent, Shader shader)
    {
        var normalDebug = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        normalDebug.name = "AprilTag Outward Normal (Blue)";
        normalDebug.transform.SetParent(parent, false);
        normalDebug.transform.localPosition = new Vector3(0f, 0.82f, 0f);
        normalDebug.transform.localScale = new Vector3(0.08f, 0.32f, 0.08f);

        var collider = normalDebug.GetComponent<Collider>();
        if (collider != null)
            Destroy(collider);

        var renderer = normalDebug.GetComponent<Renderer>();
        if (renderer != null)
        {
            renderer.material = new Material(shader);
            renderer.material.color = new Color(0.05f, 0.45f, 1f, 1f);
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }
    }

    private static void CreatePreviewEdges(Transform parent, Shader shader)
    {
        const float half = 0.5f;
        var edgeIndex = 0;

        for (var y = -1; y <= 1; y += 2)
        for (var z = -1; z <= 1; z += 2)
            CreatePreviewEdge(parent, ref edgeIndex, new Vector3(0f, y * half, z * half), new Vector3(PreviewEdgeLength, PreviewEdgeThickness, PreviewEdgeThickness), shader);

        for (var x = -1; x <= 1; x += 2)
        for (var z = -1; z <= 1; z += 2)
            CreatePreviewEdge(parent, ref edgeIndex, new Vector3(x * half, 0f, z * half), new Vector3(PreviewEdgeThickness, PreviewEdgeLength, PreviewEdgeThickness), shader);

        for (var x = -1; x <= 1; x += 2)
        for (var y = -1; y <= 1; y += 2)
            CreatePreviewEdge(parent, ref edgeIndex, new Vector3(x * half, y * half, 0f), new Vector3(PreviewEdgeThickness, PreviewEdgeThickness, PreviewEdgeLength), shader);
    }

    private static void CreatePreviewEdge(Transform parent, ref int edgeIndex, Vector3 localPosition, Vector3 localScale, Shader shader)
    {
        var edge = GameObject.CreatePrimitive(PrimitiveType.Cube);
        edge.name = $"Preview Edge {edgeIndex + 1}";
        edge.transform.SetParent(parent, false);
        edge.transform.localPosition = localPosition;
        edge.transform.localScale = localScale;

        var collider = edge.GetComponent<Collider>();
        if (collider != null)
            UnityEngine.Object.Destroy(collider);

        var renderer = edge.GetComponent<Renderer>();
        if (renderer != null)
        {
            renderer.material = new Material(shader);
            renderer.material.color = PreviewEdgeColors[edgeIndex];
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        ++edgeIndex;
    }

    private static bool IsFinite(Vector3 value)
    {
        return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
               !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
               !float.IsNaN(value.z) && !float.IsInfinity(value.z);
    }

    private void LoadDroneView()
    {
        SceneManager.LoadScene(DroneViewSceneName, LoadSceneMode.Single);
    }

    private void DisableLegacyPlacementPrototype()
    {
        var placementController = FindAnyObjectByType<ARPlacementPrototypeController>();
        if (placementController != null)
            placementController.enabled = false;

        // This older DJI-only prototype intentionally disables the AR camera.
        // It must never run alongside the phone-camera AprilTag scan flow.
        foreach (var markerController in FindObjectsByType<DJIAprilTagMarkerMvpController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            markerController.enabled = false;

        foreach (var planeManager in FindObjectsByType<ARPlaneManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            planeManager.enabled = false;

        foreach (var raycastManager in FindObjectsByType<ARRaycastManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            raycastManager.enabled = false;

        var djiBackground = FindAnyObjectByType<DJIGPUBackground>();
        if (djiBackground != null)
            djiBackground.enabled = false;

        var djiPoseDriver = FindAnyObjectByType<DJICameraPoseDriver>();
        if (djiPoseDriver != null)
            djiPoseDriver.enabled = false;

        var legacyCanvas = GameObject.Find("AR Placement Canvas");
        if (legacyCanvas != null)
            legacyCanvas.SetActive(false);

        var legacyMarkerCanvas = GameObject.Find("DJI AprilTag MVP Canvas");
        if (legacyMarkerCanvas != null)
            legacyMarkerCanvas.SetActive(false);
    }

    private void EnsurePhoneArCameraIsEnabled()
    {
        var arSession = FindAnyObjectByType<ARSession>();
        if (arSession != null)
            arSession.enabled = true;

        cameraManager ??= FindAnyObjectByType<ARCameraManager>();
        if (cameraManager == null)
            return;

        cameraManager.enabled = true;
        var targetCamera = cameraManager.GetComponent<Camera>() ?? Camera.main;
        if (targetCamera == null)
            return;

        var cameraBackground = targetCamera.GetComponent<ARCameraBackground>();
        if (cameraBackground != null)
            cameraBackground.enabled = true;
    }

    private void CreateUi()
    {
        var canvasObject = new GameObject(RuntimeCanvasName);
        var canvas = canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 1000;

        var scaler = canvasObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        canvasObject.AddComponent<GraphicRaycaster>();

        var panel = CreatePanel(canvas.transform, "Status", new Vector2(0.5f, 0f), new Vector2(0f, 72f), new Vector2(980f, 86f), new Color(0.04f, 0.07f, 0.09f, 0.72f));
        _statusLabel = CreateText(panel, "Label", 24, TextAnchor.MiddleCenter);

        var buttonPanel = CreatePanel(canvas.transform, "ConnectDroneButton", new Vector2(0.5f, 0f), new Vector2(0f, 184f), new Vector2(420f, 72f), new Color(0.05f, 0.42f, 0.32f, 0.96f));
        _connectDroneButton = buttonPanel.gameObject.AddComponent<Button>();
        _connectDroneButton.onClick.AddListener(LoadDroneView);
        var buttonText = CreateText(buttonPanel, "Label", 28, TextAnchor.MiddleCenter);
        buttonText.text = "Csatlakoztassa a drónt";
        _connectDroneButton.gameObject.SetActive(false);

        var diagnosticPanel = CreatePanel(canvas.transform, "PnpCandidateDiagnosticButton", new Vector2(0f, 1f), new Vector2(175f, -46f), new Vector2(320f, 58f), new Color(0.16f, 0.2f, 0.3f, 0.92f));
        var diagnosticButton = diagnosticPanel.gameObject.AddComponent<Button>();
        diagnosticButton.onClick.AddListener(CyclePoseCandidateDiagnosticMode);
        _candidateDiagnosticLabel = CreateText(diagnosticPanel, "Label", 20, TextAnchor.MiddleCenter);
        UpdateCandidateDiagnosticButtonLabel();

        SetStatus($"Irányítsa a telefon kameráját az AprilTag {targetTagId} markerre.");
    }

    private void CyclePoseCandidateDiagnosticMode()
    {
        poseCandidateDiagnosticMode = poseCandidateDiagnosticMode switch
        {
            PoseCandidateDiagnosticMode.Auto => PoseCandidateDiagnosticMode.ForceCandidate0,
            PoseCandidateDiagnosticMode.ForceCandidate0 => PoseCandidateDiagnosticMode.ForceCandidate1,
            _ => PoseCandidateDiagnosticMode.Auto
        };
        UpdateCandidateDiagnosticButtonLabel();
    }

    private void UpdateCandidateDiagnosticButtonLabel()
    {
        if (_candidateDiagnosticLabel == null)
            return;

        var label = poseCandidateDiagnosticMode switch
        {
            PoseCandidateDiagnosticMode.ForceCandidate0 => "PnP: Candidate 0",
            PoseCandidateDiagnosticMode.ForceCandidate1 => "PnP: Candidate 1",
            _ => "PnP: Auto"
        };
        _candidateDiagnosticLabel.text = label;
    }

    private static RectTransform CreatePanel(Transform parent, string name, Vector2 anchor, Vector2 position, Vector2 size, Color color)
    {
        var panelObject = new GameObject(name, typeof(RectTransform));
        panelObject.transform.SetParent(parent, false);
        var rect = panelObject.GetComponent<RectTransform>();
        rect.anchorMin = anchor;
        rect.anchorMax = anchor;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
        panelObject.AddComponent<Image>().color = color;
        return rect;
    }

    private static Text CreateText(RectTransform parent, string name, int fontSize, TextAnchor alignment)
    {
        var textObject = new GameObject(name, typeof(RectTransform));
        textObject.transform.SetParent(parent, false);
        var rect = textObject.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(18f, 10f);
        rect.offsetMax = new Vector2(-18f, -10f);

        var text = textObject.AddComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = fontSize;
        text.alignment = alignment;
        text.color = Color.white;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        return text;
    }

    private void SetStatus(string message)
    {
        if (_statusLabel != null)
            _statusLabel.text = message;
    }
}

internal static class PhoneAprilTagScanBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void LockLandscapeOrientation()
    {
        Screen.autorotateToPortrait = false;
        Screen.autorotateToPortraitUpsideDown = false;
        Screen.autorotateToLandscapeLeft = false;
        Screen.autorotateToLandscapeRight = false;
        Screen.orientation = ScreenOrientation.LandscapeLeft;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        if (SceneManager.GetActiveScene().name != "SampleScene")
            return;

        var camera = Camera.main ?? UnityEngine.Object.FindAnyObjectByType<Camera>();
        if (camera != null && camera.GetComponent<PhoneAprilTagScanController>() == null)
            camera.gameObject.AddComponent<PhoneAprilTagScanController>();
    }
}
