using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Renders the board-local shared cube over the unchanged DJI OES background.
/// Native PnP uses the OpenCV optical frame (+X right, +Y down, +Z forward).
/// Unity's rendering view frame is (+X right, +Y up, +Z backward), so one
/// proper Rx(180) basis maps native camera coordinates into Unity view space.
/// </summary>
[DisallowMultipleComponent]
public sealed class DjiSharedReferenceCubeOverlay : MonoBehaviour
{
    private const int OverlayLayer = 2; // Built-in Ignore Raycast layer; isolated from the video base camera at runtime.
    private const float NearClipMeters = 0.01f;
    private const float FarClipMeters = 100f;

    [SerializeField, Min(0.1f)] private float stalePoseTimeoutSeconds = 2f;
    [SerializeField, Min(0.1f)] private float poseDiagnosticIntervalSeconds = 1f;

    private Camera _videoCamera;
    private Camera _overlayCamera;
    private DjiBoardVisionProvider _boardVisionProvider;
    private GameObject _contentRoot;
    private GameObject _cube;
    private Renderer[] _contentRenderers;
    private RoofCanopyProjectedShadows _projectedShadows;
    private bool _projectionLogged;
    private bool _cubeVisible;
    private bool _hasAppliedContentVisibility;
    private bool _renderersVisible;
    private bool _hasRoofCanopyContent;
    private bool _modelVisibleByUser = true;
    private bool _hasAuthoritativeVisualAcquisition;
    private bool _stalePoseLogged;
    private float _nextPoseDiagnosticTime;
    private DjiRenderPoseSource _lastRenderPoseSource = DjiRenderPoseSource.None;

    private static readonly Matrix4x4 UnityViewFromNativeOptical =
        Matrix4x4.Rotate(Quaternion.AngleAxis(180f, Vector3.right));

    /// <summary>
    /// The single native-optical to Unity-view basis used by the production
    /// overlay. Shadow diagnostics reuse this value without participating in
    /// camera routing or rendering.
    /// </summary>
    public static Matrix4x4 NativeOpticalToUnityViewBasis => UnityViewFromNativeOptical;

    // Read-only HUD observation of the already applied overlay, not another pose query.
    public DjiRenderPoseSource DisplayedRenderPoseSource =>
        (!_hasRoofCanopyContent || _modelVisibleByUser) ? TrackingRenderPoseSource : DjiRenderPoseSource.None;

    // Intentional presentation hiding does not remove the already applied pose's authority.
    public DjiRenderPoseSource TrackingRenderPoseSource =>
        isActiveAndEnabled && _cubeVisible && _contentRoot != null && _contentRoot.activeInHierarchy &&
        _overlayCamera != null && _overlayCamera.isActiveAndEnabled &&
        _videoCamera != null && _videoCamera.isActiveAndEnabled
            ? _lastRenderPoseSource
            : DjiRenderPoseSource.None;

    public bool HasAuthoritativeVisualReference => _hasAuthoritativeVisualAcquisition;
    public bool HasRoofCanopyContent => _hasRoofCanopyContent && _cube != null;
    public bool ModelVisibleByUser => _modelVisibleByUser;

    public void SetModelVisibleByUser(bool visible)
    {
        if (_modelVisibleByUser == visible)
            return;
        _modelVisibleByUser = visible;
        ApplyContentVisibility();
    }

    public void Configure(Camera videoCamera, DjiBoardVisionProvider boardVisionProvider)
    {
        _videoCamera = videoCamera;
        if (_boardVisionProvider != boardVisionProvider)
        {
            if (_boardVisionProvider != null)
                _boardVisionProvider.AuthoritativeVisualPoseAccepted -= OnAuthoritativeVisualPoseAccepted;
            _boardVisionProvider = boardVisionProvider;
            _hasAuthoritativeVisualAcquisition = boardVisionProvider != null &&
                                                boardVisionProvider.HasHighConfidenceCameraFromBoard;
            if (_boardVisionProvider != null)
                _boardVisionProvider.AuthoritativeVisualPoseAccepted += OnAuthoritativeVisualPoseAccepted;
        }
        EnsureOverlayCamera();
        EnsureSharedContent();
        SetCubeVisible(false, "WAITING_FOR_VALID_BOARD_POSE");
    }

    private void OnAuthoritativeVisualPoseAccepted(Pose cameraFromBoard, long sourceTimestampNs)
    {
        if (sourceTimestampNs > 0L)
            _hasAuthoritativeVisualAcquisition = true;
    }

    private void OnDestroy()
    {
        if (_boardVisionProvider != null)
            _boardVisionProvider.AuthoritativeVisualPoseAccepted -= OnAuthoritativeVisualPoseAccepted;
    }

    private void LateUpdate()
    {
        if (_overlayCamera == null || _boardVisionProvider == null)
            return;

        if (!_boardVisionProvider.TryGetRenderCameraFromBoard(
                stalePoseTimeoutSeconds,
                out var cameraFromBoard,
                out var renderPoseSource,
                out var poseAgeSeconds))
        {
            SetCubeVisible(false, _boardVisionProvider.HasAcceptedCameraFromBoard ? "BOARD_POSE_STALE" : "NO_ACCEPTED_BOARD_POSE");
            if (!_stalePoseLogged)
            {
                _stalePoseLogged = true;
                Debug.LogWarning($"DJI_SHARED_CUBE_POSE_STALE ageMs={_boardVisionProvider.AcceptedCameraFromBoardAgeSeconds * 1000f:F0}");
            }
            LogRenderPoseSource(DjiRenderPoseSource.None, float.PositiveInfinity);
            return;
        }

        // Drone mode owns its board frame. No phone ARCore world pose is needed
        // to render T_camera_cube = T_camera_board * T_board_cube.
        ApplyStandaloneBoardOverlayPose(cameraFromBoard);
        // Capture the actual Unity-side application point, after the resolved
        // visual pose has been assigned to the overlay camera/content.
        _boardVisionProvider.RecordVisualPoseAppliedForRender(cameraFromBoard);
        _stalePoseLogged = false;
        SetCubeVisible(true, renderPoseSource == DjiRenderPoseSource.Visual
            ? "VALID_BOARD_POSE_STANDALONE_BOARD_FRAME"
            : "TELEMETRY_PROPAGATED_PERSISTENT_WORLD_POSE");
        LogRenderPoseSource(renderPoseSource, poseAgeSeconds);

        if (Time.unscaledTime >= _nextPoseDiagnosticTime)
        {
            _nextPoseDiagnosticTime = Time.unscaledTime + poseDiagnosticIntervalSeconds;
            LogSharedCubeValidation(cameraFromBoard, poseAgeSeconds, renderPoseSource);
        }
    }

    private void LogRenderPoseSource(DjiRenderPoseSource source, float ageSeconds)
    {
        if (_lastRenderPoseSource == source)
            return;

        _lastRenderPoseSource = source;
        var age = float.IsFinite(ageSeconds) ? $" ageMs={ageSeconds * 1000f:F0}" : string.Empty;
        Debug.Log($"DJI_RENDER_POSE_SOURCE={source.ToString().ToUpperInvariant()}{age}");
    }

    private void EnsureOverlayCamera()
    {
        if (_videoCamera == null)
        {
            Debug.LogError("DJI_SHARED_CUBE_OVERLAY_FAILED reason=VIDEO_CAMERA_UNAVAILABLE");
            return;
        }

        if (_overlayCamera != null)
            return;

        var overlayObject = new GameObject("DJI Shared Cube Overlay Camera", typeof(Camera), typeof(UniversalAdditionalCameraData));
        _overlayCamera = overlayObject.GetComponent<Camera>();
        overlayObject.AddComponent<DjiOverlayCullingParity>();
        _overlayCamera.clearFlags = CameraClearFlags.Depth;
        _overlayCamera.cullingMask = 1 << OverlayLayer;
        _overlayCamera.nearClipPlane = NearClipMeters;
        _overlayCamera.farClipPlane = FarClipMeters;
        _overlayCamera.allowHDR = _videoCamera.allowHDR;
        _overlayCamera.allowMSAA = _videoCamera.allowMSAA;
        _overlayCamera.useOcclusionCulling = false;

        // The base camera keeps rendering the OES background. Exclude only the
        // runtime overlay layer so the cube is not rendered twice.
        _videoCamera.cullingMask &= ~(1 << OverlayLayer);
        var baseCameraData = _videoCamera.GetComponent<UniversalAdditionalCameraData>() ??
                             _videoCamera.gameObject.AddComponent<UniversalAdditionalCameraData>();
        var overlayCameraData = _overlayCamera.GetComponent<UniversalAdditionalCameraData>();
        overlayCameraData.renderType = CameraRenderType.Overlay;
        overlayCameraData.renderPostProcessing = false;
        if (!baseCameraData.cameraStack.Contains(_overlayCamera))
            baseCameraData.cameraStack.Add(_overlayCamera);

        Debug.Log("DJI_SHARED_CUBE_OVERLAY_CAMERA_CREATED background=OES overlay=Unity3D");
    }

    private void EnsureSharedContent()
    {
        if (_contentRoot != null)
            return;

        _contentRoot = new GameObject(ReferenceBoardContentDefinition.SharedContentRootName);
        _cube = ARContentFactory.CreateSelected(_contentRoot.transform, OverlayLayer);
        _contentRenderers = _cube.GetComponentsInChildren<Renderer>(true);
        _hasRoofCanopyContent = ARContentSelection.Selected == ARContentType.RoofCanopy;
        if (_hasRoofCanopyContent)
            _projectedShadows = RoofCanopyProjectedShadows.Create(_contentRoot.transform, OverlayLayer);
        _hasAppliedContentVisibility = false;
        SetCubeVisible(false, "WAITING_FOR_VALID_BOARD_POSE");
        ReferenceBoardDebugGeometry.CreateBoardFrame(
            "DJI Board Frame",
            _contentRoot.transform,
            OverlayLayer,
            includeMarkerCenters: true);
        if (ARContentSelection.Selected == ARContentType.Cube)
        {
            ReferenceBoardContentDefinition.LogBoardLocal("[DJI Shared Cube]");
            ReferenceBoardContentDefinition.LogDefinitionAndBoardContact("[DJI Shared Cube]");
        }
        Debug.Log($"DJI_BOARD_CONTENT_SCALE type={ARContentSelection.Selected} scale={_cube.transform.lossyScale} localScale={_cube.transform.localScale}");
        Debug.Log("DJI_SHARED_CUBE_BOARD_OVERLAY_CREATED origin=T_board +X=red +Y=green +Z=blue rectangle=0.360x0.360m");
    }

    private void ApplyStandaloneBoardOverlayPose(Pose cameraFromBoard)
    {
        // Unity scene coordinates are intentionally the physical board frame in
        // standalone DroneView. This is not an ARCore-world substitute.
        _contentRoot.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

        ApplyIntrinsicProjection();

        // T_unityView_board = B_unityView_nativeOptical * T_cameraCv_board.
        // B is the single documented native-camera-to-Unity-view basis map.
        var unityViewFromBoard = UnityViewFromNativeOptical *
                                 ReferenceFrameTransforms.MatrixFromPose(cameraFromBoard);
        _overlayCamera.worldToCameraMatrix = unityViewFromBoard;
        _overlayCamera.cullingMatrix = _overlayCamera.projectionMatrix * unityViewFromBoard;

        // This transform is diagnostic/culling metadata only; rendering uses
        // the explicit worldToCameraMatrix above so no hidden Euler correction
        // can enter the native optical-frame conversion.
        var boardFromCamera = ReferenceFrameTransforms.Invert(cameraFromBoard);
        _overlayCamera.transform.SetPositionAndRotation(boardFromCamera.position, boardFromCamera.rotation);

        ValidateStandaloneTransformInvariant(cameraFromBoard);
        if (!ReferenceFrameTransforms.ValidateCameraBoardRoundTrip(cameraFromBoard, out var roundTrip))
            Debug.LogError($"DJI_CAMERA_BOARD_ROUND_TRIP_FAILED {roundTrip}");
    }

    private void ApplyIntrinsicProjection()
    {
        var left = -DjiFc3582CameraModel.Cx / DjiFc3582CameraModel.Fx * NearClipMeters;
        var right = (DjiFc3582CameraModel.ImageWidth - DjiFc3582CameraModel.Cx) / DjiFc3582CameraModel.Fx * NearClipMeters;
        var bottom = -(DjiFc3582CameraModel.ImageHeight - DjiFc3582CameraModel.Cy) / DjiFc3582CameraModel.Fy * NearClipMeters;
        var top = DjiFc3582CameraModel.Cy / DjiFc3582CameraModel.Fy * NearClipMeters;
        var projection = Matrix4x4.Frustum(left, right, bottom, top, NearClipMeters, FarClipMeters);
        _overlayCamera.projectionMatrix = projection;
        _overlayCamera.nonJitteredProjectionMatrix = projection;

        if (_projectionLogged)
            return;

        _projectionLogged = true;
        Debug.Log(
            $"DJI_OVERLAY_PROJECTION width={DjiFc3582CameraModel.ImageWidth} height={DjiFc3582CameraModel.ImageHeight} " +
            $"fx={DjiFc3582CameraModel.Fx:F1} fy={DjiFc3582CameraModel.Fy:F1} cx={DjiFc3582CameraModel.Cx:F1} cy={DjiFc3582CameraModel.Cy:F1} " +
            "rotation=0 crop=NONE resize=NONE mirror=False");
        Debug.Log("DJI_OVERLAY_CAMERA_BASIS native=(right,down,forward) unityView=(right,up,backward) conversion=Rx180");
    }

    private void LogSharedCubeValidation(Pose cameraFromBoard, float poseAgeSeconds, DjiRenderPoseSource renderPoseSource)
    {
        if (ARContentSelection.Selected != ARContentType.Cube)
        {
            Debug.Log(
                $"SHARED_CONTENT_VALIDATION type={ARContentSelection.Selected} " +
                $"T_camera_board position={cameraFromBoard.position} rotation={cameraFromBoard.rotation.eulerAngles} " +
                $"poseAgeMs={poseAgeSeconds * 1000f:F0} renderPoseSource={renderPoseSource}");
            return;
        }

        var cameraFromBoardMatrix = ReferenceFrameTransforms.MatrixFromPose(cameraFromBoard);
        var cameraFromCube = cameraFromBoardMatrix *
                             ReferenceFrameTransforms.MatrixFromPose(ReferenceBoardContentDefinition.BoardFromDummyCube);
        var cameraFromCubePose = ReferenceFrameTransforms.PoseFromMatrix(cameraFromCube);
        var projectedCubeCenter = ProjectNativeCameraPoint(cameraFromCubePose.position);
        var projectedBoardOrigin = ProjectNativeCameraPoint(cameraFromBoard.position);
        var result = _boardVisionProvider.LatestResult;
        var reprojectionRms = result != null ? result.reprojectionRms : float.NaN;

        Debug.Log(
            "SHARED_CUBE_VALIDATION " +
            $"T_board_cube position={ReferenceBoardContentDefinition.BoardFromDummyCube.position} rotation={ReferenceBoardContentDefinition.BoardFromDummyCube.rotation.eulerAngles} " +
            $"T_camera_board position={cameraFromBoard.position} " +
            $"T_camera_cube position={cameraFromCubePose.position} " +
            $"projectedCubeCenterPx={FormatPixel(projectedCubeCenter)} " +
            $"boardOriginPx={FormatPixel(projectedBoardOrigin)} " +
            $"reprojectionRms={reprojectionRms:F3} poseAgeMs={poseAgeSeconds * 1000f:F0} " +
            $"renderPoseSource={renderPoseSource}");

        Debug.Log($"DJI_CAMERA_FROM_BOARD={ReferenceFrameTransforms.MatrixFromPose(cameraFromBoard)} ageMs={poseAgeSeconds * 1000f:F0}");
        Debug.Log($"DJI_BOARD_FROM_CAMERA={ReferenceFrameTransforms.MatrixFromPose(ReferenceFrameTransforms.Invert(cameraFromBoard))}");
    }

    private Vector2 ProjectNativeCameraPoint(Vector3 nativeCameraPoint)
    {
        if (nativeCameraPoint.z <= 0f)
            return new Vector2(float.NaN, float.NaN);

        return new Vector2(
            DjiFc3582CameraModel.Fx * nativeCameraPoint.x / nativeCameraPoint.z + DjiFc3582CameraModel.Cx,
            DjiFc3582CameraModel.Fy * nativeCameraPoint.y / nativeCameraPoint.z + DjiFc3582CameraModel.Cy);
    }

    private static string FormatPixel(Vector2 pixel) =>
        float.IsFinite(pixel.x) && float.IsFinite(pixel.y)
            ? $"({pixel.x:F1},{pixel.y:F1})"
            : "(invalid)";

    private static void ValidateStandaloneTransformInvariant(Pose cameraFromBoard)
    {
        var boardFromCube = ReferenceFrameTransforms.MatrixFromPose(ReferenceBoardContentDefinition.BoardFromDummyCube);
        var cameraFromBoardMatrix = ReferenceFrameTransforms.MatrixFromPose(cameraFromBoard);
        var cameraFromCubeDirect = cameraFromBoardMatrix * boardFromCube;
        var directPose = ReferenceFrameTransforms.PoseFromMatrix(cameraFromCubeDirect);
        if (!float.IsFinite(directPose.position.x) || !float.IsFinite(directPose.position.y) || !float.IsFinite(directPose.position.z))
            Debug.LogError("SHARED_CUBE_TRANSFORM_MISMATCH classification=SHARED_CONTENT_TRANSFORM_MISMATCH reason=NONFINITE_CAMERA_CUBE_POSE");
    }

    private void SetCubeVisible(bool visible, string reason)
    {
        visible &= _hasAuthoritativeVisualAcquisition;
        bool changed = _cubeVisible != visible || !_hasAppliedContentVisibility;
        _cubeVisible = visible;
        ApplyContentVisibility();
        if (changed)
            Debug.Log($"DJI_SHARED_CUBE_VISIBLE visible={visible} reason={reason}");
    }

    private void ApplyContentVisibility()
    {
        bool visible = _cubeVisible && (!_hasRoofCanopyContent || _modelVisibleByUser);
        if (_projectedShadows != null)
            _projectedShadows.SetVisible(visible);
        if (_contentRenderers == null || _contentRenderers.Length == 0 ||
            (_hasAppliedContentVisibility && _renderersVisible == visible))
            return;
        _hasAppliedContentVisibility = true;
        _renderersVisible = visible;
        foreach (var renderer in _contentRenderers)
            if (renderer != null)
                renderer.enabled = visible;
    }
}
