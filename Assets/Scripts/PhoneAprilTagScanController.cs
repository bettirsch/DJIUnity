using System.Collections;
using System.Collections.Generic;
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
    private const int MaximumCameraPoseSamples = 12;
    private const long MaximumImagePoseTimestampDeltaNs = 50_000_000L;
    private const float MinimumCameraFacingDot = 0.15f;
    private const float MinimumWorldNormalContinuityDot = 0.3f;
    private const float CandidateSwitchPenalty = 0.35f;
    private const float DebugAxisLengthMeters = 0.07f;
    private const float DebugAxisThicknessMeters = 0.008f;
    private static readonly Vector3 DebugAxisOrigin = new(-0.085f, -0.085f, 0.002f);

    [SerializeField] private ARCameraManager cameraManager;
    [SerializeField] [Min(0.01f)] private float detectionIntervalSeconds = 0.03f;
    [SerializeField] [Min(1)] private int confirmationsRequired = 3;
    [SerializeField] private int targetTagId;

    private readonly float[] _nativeDetection = new float[12];
    private readonly float[] _nativePoseCandidates = new float[PoseCandidateStride * MaximumPoseCandidates];
    private readonly List<CameraPoseSample> _cameraPoseSamples = new(MaximumCameraPoseSamples);
    private Text _statusLabel;
    private Button _connectDroneButton;
    private GameObject _previewCube;
    private GameObject _markerAxes;
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
    private int _selectedPoseCandidateIndex = -1;

    private readonly struct CameraPoseSample
    {
        public CameraPoseSample(long timestampNs, Pose pose)
        {
            TimestampNs = timestampNs;
            Pose = pose;
        }

        public long TimestampNs { get; }
        public Pose Pose { get; }
    }

    private void Awake()
    {
        DisableLegacyPlacementPrototype();
        cameraManager ??= FindAnyObjectByType<ARCameraManager>();
        CreateUi();
        AprilTagScanSession.Clear();
    }

    private void OnEnable()
    {
        DJIAprilTagNative.SetTargetTagId(targetTagId);
        cameraManager ??= FindAnyObjectByType<ARCameraManager>();
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

    private void OnCameraFrameReceived(ARCameraFrameEventArgs frame)
    {
        RecordCameraPose(frame.timestampNs);

        if (cameraManager != null && cameraManager.TryGetIntrinsics(out var intrinsics) &&
            intrinsics.resolution.x > 0 && intrinsics.resolution.y > 0 &&
            intrinsics.focalLength.x > 0f && intrinsics.focalLength.y > 0f)
        {
            _cachedIntrinsics = intrinsics;
            _hasCachedIntrinsics = true;
        }
    }

    private void RecordCameraPose(long? timestampNs)
    {
        if (!timestampNs.HasValue)
            return;

        var targetCamera = cameraManager != null ? cameraManager.GetComponent<Camera>() : Camera.main;
        if (targetCamera == null)
            return;

        var sample = new CameraPoseSample(timestampNs.Value, new Pose(targetCamera.transform.position, targetCamera.transform.rotation));
        if (_cameraPoseSamples.Count > 0 && _cameraPoseSamples[^1].TimestampNs == sample.TimestampNs)
        {
            _cameraPoseSamples[^1] = sample;
            return;
        }

        _cameraPoseSamples.Add(sample);
        if (_cameraPoseSamples.Count > MaximumCameraPoseSamples)
            _cameraPoseSamples.RemoveAt(0);
    }

    private bool TryGetSynchronizedCameraPose(double imageTimestampSeconds, out Pose cameraPose, out long timestampDeltaNs)
    {
        cameraPose = default;
        timestampDeltaNs = long.MaxValue;
        if (imageTimestampSeconds <= 0d || _cameraPoseSamples.Count == 0)
            return false;

        var imageTimestampNs = checked((long)System.Math.Round(imageTimestampSeconds * 1_000_000_000d));
        foreach (var sample in _cameraPoseSamples)
        {
            var delta = System.Math.Abs(sample.TimestampNs - imageTimestampNs);
            if (delta >= timestampDeltaNs)
                continue;

            cameraPose = sample.Pose;
            timestampDeltaNs = delta;
        }

        return timestampDeltaNs <= MaximumImagePoseTimestampDeltaNs;
    }

    private void Update()
    {
        // A stale pose would make the cube appear attached even after the marker leaves the frame.
        if (_previewCube != null && _previewCube.activeSelf &&
            Time.unscaledTime - _lastPoseUpdateTime > MarkerLostTimeoutSeconds)
        {
            _previewCube.SetActive(false);
            _selectedPoseCandidateIndex = -1;
            if (_markerAxes != null)
                _markerAxes.SetActive(false);
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
            {
                SetStatus("A telefon kamerája nem érhető el.");
                yield return new WaitForSecondsRealtime(detectionIntervalSeconds);
                continue;
            }

            if (!cameraManager.TryAcquireLatestCpuImage(out var image))
            {
                SetStatus("Telefonkamera indítása...");
                yield return new WaitForSecondsRealtime(detectionIntervalSeconds);
                continue;
            }

            try
            {
                if (!TryGetSynchronizedCameraPose(image.timestamp, out var cameraPose, out var poseTimestampDeltaNs))
                {
                    SetStatus("A CPU-képhez illeszkedő ARCore kamera-pózra várakozás...");
                    yield return new WaitForSecondsRealtime(detectionIntervalSeconds);
                    continue;
                }

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
                        _hasTagPose = TrySelectWorldPose(poseCandidateCount, cameraPose, poseTimestampDeltaNs);
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
                else
                {
                    if (_markerConfirmed)
                    {
                        SetStatus("Tartsa az AprilTag-et a telefon kameraképében.");
                    }
                    else
                    {
                        _consecutiveMatches = 0;
                        SetStatus($"Irányítsa a telefon kameráját az AprilTag {targetTagId} markerre.");
                    }
                }
            }
            finally
            {
                image.Dispose();
            }

            yield return new WaitForSecondsRealtime(detectionIntervalSeconds);
        }
#endif
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

    private bool TrySelectWorldPose(int poseCandidateCount, Pose cameraPose, long poseTimestampDeltaNs)
    {
        var bestScore = float.PositiveInfinity;
        var hasBestCandidate = false;
        var bestCandidateIndex = -1;
        var bestCameraFacingDot = 0f;
        var bestTagPosition = Vector3.zero;
        var bestTagRotation = Quaternion.identity;
        var bestCubePosition = Vector3.zero;
        var bestCubeRotation = Quaternion.identity;
        var candidateLimit = Mathf.Min(poseCandidateCount, MaximumPoseCandidates);

        for (var candidateIndex = 0; candidateIndex < candidateLimit; ++candidateIndex)
        {
            var offset = candidateIndex * PoseCandidateStride;
            var reprojectionError = _nativePoseCandidates[offset + 12];
            if (float.IsNaN(reprojectionError) || float.IsInfinity(reprojectionError))
                continue;

            // OpenCV uses right/down/forward camera coordinates; Unity uses right/up/forward.
            var tagPosition = new Vector3(
                _nativePoseCandidates[offset],
                -_nativePoseCandidates[offset + 1],
                _nativePoseCandidates[offset + 2]);
            var tagRight = new Vector3(
                _nativePoseCandidates[offset + 3],
                -_nativePoseCandidates[offset + 6],
                _nativePoseCandidates[offset + 9]).normalized;
            var tagUp = Vector3.ProjectOnPlane(new Vector3(
                _nativePoseCandidates[offset + 4],
                -_nativePoseCandidates[offset + 7],
                _nativePoseCandidates[offset + 10]), tagRight).normalized;
            var tagNormal = Vector3.Cross(tagRight, tagUp).normalized;
            if (!IsFinite(tagPosition) || !IsFinite(tagRight) || !IsFinite(tagUp) || !IsFinite(tagNormal) ||
                tagRight.sqrMagnitude < 0.9f || tagUp.sqrMagnitude < 0.9f || tagNormal.sqrMagnitude < 0.9f)
            {
                continue;
            }

            var cameraFacingDot = -Vector3.Dot(tagNormal, tagPosition.normalized);
            if (cameraFacingDot < MinimumCameraFacingDot)
                continue;

            var tagWorldPosition = cameraPose.position + cameraPose.rotation * tagPosition;
            var tagWorldRotation = cameraPose.rotation * Quaternion.LookRotation(tagNormal, tagUp);
            var tagWorldNormal = tagWorldRotation * Vector3.forward;
            if (_hasTrackedTagWorldPose &&
                Vector3.Dot(tagWorldNormal, _trackedTagWorldRotation * Vector3.forward) < MinimumWorldNormalContinuityDot)
            {
                continue;
            }

            var cubeWorldPosition = cameraPose.position + cameraPose.rotation * (tagPosition + tagNormal * (PreviewCubeSizeMeters * 0.5f));
            // Convert from the marker basis (forward = outward normal) to a Unity cube
            // basis (up = outward normal), so the cube's bottom face lies on the tag.
            var cubeWorldRotation = tagWorldRotation * Quaternion.AngleAxis(90f, Vector3.right);

            // A fixed marker should retain the same ARCore world pose between frames.
            var score = reprojectionError * 0.02f;
            if (_hasTrackedTagWorldPose)
            {
                score += Vector3.Distance(tagWorldPosition, _trackedTagWorldPosition) / 0.04f;
                score += Quaternion.Angle(tagWorldRotation, _trackedTagWorldRotation) / 15f;
            }
            if (_selectedPoseCandidateIndex >= 0 && candidateIndex != _selectedPoseCandidateIndex)
                score += CandidateSwitchPenalty;

            if (score >= bestScore)
                continue;

            bestScore = score;
            hasBestCandidate = true;
            bestCandidateIndex = candidateIndex;
            bestCameraFacingDot = cameraFacingDot;
            bestTagPosition = tagWorldPosition;
            bestTagRotation = tagWorldRotation;
            bestCubePosition = cubeWorldPosition;
            bestCubeRotation = cubeWorldRotation;
        }

        if (!hasBestCandidate)
        {
            if (_hasTrackedTagWorldPose)
            {
                Debug.LogWarning("AprilTag pose normal flip rejected; retaining the last valid marker world pose.");
                return true;
            }

            return false;
        }

        _trackedTagWorldPosition = bestTagPosition;
        _trackedTagWorldRotation = bestTagRotation;
        _selectedCubeWorldPosition = bestCubePosition;
        _selectedCubeWorldRotation = bestCubeRotation;
        _hasTrackedTagWorldPose = true;
        var candidateChanged = _selectedPoseCandidateIndex != bestCandidateIndex;
        _selectedPoseCandidateIndex = bestCandidateIndex;
        UpdateMarkerAxes(bestTagPosition, bestTagRotation);
        if (candidateChanged)
        {
            Debug.Log($"AprilTag pose candidate {bestCandidateIndex} selected: reprojection score {bestScore:F3}, camera-facing {bestCameraFacingDot:F3}, image-pose delta {poseTimestampDeltaNs / 1_000_000f:F1} ms.");
        }
        return true;
    }

    private void UpdateMarkerAxes(Vector3 markerWorldPosition, Quaternion markerWorldRotation)
    {
        EnsureMarkerAxes();
        if (_markerAxes == null)
            return;

        _markerAxes.transform.SetPositionAndRotation(
            markerWorldPosition + markerWorldRotation * DebugAxisOrigin,
            markerWorldRotation);
        _markerAxes.SetActive(true);
    }

    private void EnsureMarkerAxes()
    {
        if (_markerAxes != null)
            return;

        var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
            return;

        _markerAxes = new GameObject("AprilTag Debug Axes");
        CreateDebugAxis(_markerAxes.transform, "X", Vector3.right, Color.red, shader);
        CreateDebugAxis(_markerAxes.transform, "Y", Vector3.up, Color.green, shader);
        CreateDebugAxis(_markerAxes.transform, "Normal", Vector3.forward, Color.blue, shader);
    }

    private static void CreateDebugAxis(Transform parent, string name, Vector3 direction, Color color, Shader shader)
    {
        var axis = GameObject.CreatePrimitive(PrimitiveType.Cube);
        axis.name = $"AprilTag Axis {name}";
        axis.transform.SetParent(parent, false);
        axis.transform.localPosition = direction * (DebugAxisLengthMeters * 0.5f);
        axis.transform.localScale = new Vector3(
            direction.x != 0f ? DebugAxisLengthMeters : DebugAxisThicknessMeters,
            direction.y != 0f ? DebugAxisLengthMeters : DebugAxisThicknessMeters,
            direction.z != 0f ? DebugAxisLengthMeters : DebugAxisThicknessMeters);

        var collider = axis.GetComponent<Collider>();
        if (collider != null)
            UnityEngine.Object.Destroy(collider);

        var renderer = axis.GetComponent<Renderer>();
        if (renderer != null)
        {
            renderer.material = new Material(shader);
            renderer.material.color = color;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }
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
