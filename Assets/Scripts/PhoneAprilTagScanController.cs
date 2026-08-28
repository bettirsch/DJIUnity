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

    [SerializeField] private ARCameraManager cameraManager;
    [SerializeField] [Min(0.01f)] private float detectionIntervalSeconds = 0.03f;
    [SerializeField] [Min(1)] private int confirmationsRequired = 3;
    [SerializeField] private int targetTagId;

    private readonly float[] _nativeDetection = new float[12];
    private readonly float[] _nativePoseCandidates = new float[PoseCandidateStride * MaximumPoseCandidates];
    private Text _statusLabel;
    private Button _connectDroneButton;
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
    private bool _hasReceivedCameraFrame;
    private float _cameraStartupTime;
    private float _lastCpuImageScanTime = float.NegativeInfinity;

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
        var frameCameraPose = new Pose(targetCamera.transform.position, targetCamera.transform.rotation);

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
            ProcessCpuImage(image, frameCameraPose);
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

    private void ProcessCpuImage(XRCpuImage image, Pose frameCameraPose)
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
                var poseCandidateCount = DJIAprilTagNative.TryDetectPoseCandidates(
                    rgbaBytes,
                    conversion.outputDimensions.x,
                    conversion.outputDimensions.y,
                    fx,
                    fy,
                    cx,
                    cy,
                    PrintedTagSizeMeters,
                    _nativeDetection,
                    _nativePoseCandidates);
                if (Mathf.RoundToInt(_nativeDetection[0]) == targetTagId)
                    LogCpuImageDiagnostics(image, conversion, fx, fy, cx, cy, poseCandidateCount);
                _hasTagPose = TrySelectWorldPose(poseCandidateCount, frameCameraPose);
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
        int poseCandidateCount)
    {
        // The native detector receives precisely this unrotated converted buffer.
        Debug.Log(
            $"[DJIAprilTag] CPU/PnP input source={image.width}x{image.height} " +
            $"converted={conversion.outputDimensions.x}x{conversion.outputDimensions.y} " +
            $"format=RGBA32 transform=None (no rotation, crop, or mirror) " +
            $"ARCoreIntrinsics={_cachedIntrinsics.resolution.x}x{_cachedIntrinsics.resolution.y} " +
            $"rawFxFy=({_cachedIntrinsics.focalLength.x:F3},{_cachedIntrinsics.focalLength.y:F3}) " +
            $"rawCxCy=({_cachedIntrinsics.principalPoint.x:F3},{_cachedIntrinsics.principalPoint.y:F3}) " +
            $"passedFxFyCxCy=({fx:F3},{fy:F3},{cx:F3},{cy:F3}) " +
            $"IPPECandidates={poseCandidateCount}");
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

    private bool TrySelectWorldPose(int poseCandidateCount, Pose cameraPose)
    {
        var bestScore = float.PositiveInfinity;
        var hasBestCandidate = false;
        var bestTagPosition = Vector3.zero;
        var bestCubePosition = Vector3.zero;
        var bestCubeRotation = Quaternion.identity;
        var bestCandidateIndex = -1;
        var candidateLimit = Mathf.Min(poseCandidateCount, MaximumPoseCandidates);

        for (var candidateIndex = 0; candidateIndex < candidateLimit; ++candidateIndex)
        {
            var offset = candidateIndex * PoseCandidateStride;
            var reprojectionError = _nativePoseCandidates[offset + 12];
            if (float.IsNaN(reprojectionError) || float.IsInfinity(reprojectionError))
                continue;

            if (!TryConvertNativePose(offset, out var tagPosition, out var cubeCameraRotation, out var outwardNormal))
                continue;

            var tagWorldPosition = cameraPose.position + cameraPose.rotation * tagPosition;
            var cubeWorldPosition = cameraPose.position + cameraPose.rotation * (tagPosition + outwardNormal * (PreviewCubeSizeMeters * 0.5f));
            var cubeWorldRotation = cameraPose.rotation * cubeCameraRotation;

            // A fixed marker should retain the same ARCore world pose between frames.
            var score = reprojectionError * 0.02f;
            if (_hasTrackedTagWorldPose)
            {
                score += Vector3.Distance(tagWorldPosition, _trackedTagWorldPosition) / 0.04f;
                score += Quaternion.Angle(cubeWorldRotation, _trackedTagWorldRotation) / 15f;
            }

            if (score >= bestScore)
                continue;

            bestScore = score;
            hasBestCandidate = true;
            bestTagPosition = tagWorldPosition;
            bestCubePosition = cubeWorldPosition;
            bestCubeRotation = cubeWorldRotation;
            bestCandidateIndex = candidateIndex;
        }

        if (!hasBestCandidate)
        {
            Debug.Log("[DJIAprilTag] Unity rejected every raw OpenCV PnP candidate before world-pose selection.");
            return false;
        }

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
        _trackedTagWorldPosition = bestTagPosition;
        _trackedTagWorldRotation = bestCubeRotation;
        _selectedCubeWorldPosition = bestCubePosition;
        _selectedCubeWorldRotation = bestCubeRotation;
        _hasTrackedTagWorldPose = true;
        Debug.Log($"[DJIAprilTag] Unity selected raw OpenCV PnP candidate={bestCandidateIndex} score={bestScore:F4}.");
        return true;
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

        SetStatus($"Irányítsa a telefon kameráját az AprilTag {targetTagId} markerre.");
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

        var camera = Camera.main ?? Object.FindAnyObjectByType<Camera>();
        if (camera != null && camera.GetComponent<PhoneAprilTagScanController>() == null)
            camera.gameObject.AddComponent<PhoneAprilTagScanController>();
    }
}
