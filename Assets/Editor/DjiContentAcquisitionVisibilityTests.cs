using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public sealed class DjiContentAcquisitionVisibilityTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private ARContentType _selection;
    private GameObject _overlayObject;
    private GameObject _providerObject;
    private GameObject _videoObject;
    private DjiSharedReferenceCubeOverlay _overlay;
    private DjiBoardVisionProvider _provider;
    private ARContentType _previousSelection;

    [SetUp]
    public void SetUp() => _previousSelection = ARContentSelection.Selected;

    private void CreateOverlay(ARContentType selection = ARContentType.RoofCanopy)
    {
        _selection = selection;
        ARContentSelection.Set(_selection);
        _videoObject = new GameObject("Visibility test video camera", typeof(Camera));
        _providerObject = new GameObject("Visibility test provider");
        _provider = _providerObject.AddComponent<DjiBoardVisionProvider>();
        _overlayObject = new GameObject("Visibility test overlay");
        _overlay = _overlayObject.AddComponent<DjiSharedReferenceCubeOverlay>();
        _overlay.Configure(_videoObject.GetComponent<Camera>(), _provider);
    }

    [TearDown]
    public void TearDown()
    {
        var content = Field<GameObject>("_contentRoot");
        var camera = Field<Camera>("_overlayCamera");
        UnityEngine.Object.DestroyImmediate(_overlayObject);
        if (content != null) UnityEngine.Object.DestroyImmediate(content);
        if (camera != null) UnityEngine.Object.DestroyImmediate(camera.gameObject);
        UnityEngine.Object.DestroyImmediate(_providerObject);
        UnityEngine.Object.DestroyImmediate(_videoObject);
        ARContentSelection.Set(_previousSelection);
    }

    [Test]
    public void Creation_WithCachedFalse_ActuallyDisablesEveryModelRenderer()
    {
        CreateOverlay();
        Assert.That(Field<bool>("_cubeVisible"), Is.False);
        Assert.That(Field<GameObject>("_cube").name, Is.EqualTo(
            _selection == ARContentType.RoofCanopy ? "RoofCanopyPlacement" : ReferenceBoardContentDefinition.DummyCubeName));
        AssertVisibility(false);
    }

    [Test]
    public void TelemetryOrPendingVisual_CannotShowContentBeforeAuthoritativeAcquisition()
    {
        CreateOverlay();
        RequestVisibility(true);
        AssertVisibility(false);
        Assert.That(_provider.HasHighConfidenceCameraFromBoard, Is.False);
    }

    [Test]
    public void AuthoritativeVisualEvent_AllowsVisibilityOnTheNextResolvedRender()
    {
        CreateOverlay();
        PublishAuthoritativeVisual();
        AssertVisibility(false);
        RequestVisibility(true);
        AssertVisibility(true);
    }

    [Test]
    public void LaterVisualLoss_DoesNotClearPermissionForPropagatedRendering()
    {
        CreateOverlay();
        PublishAuthoritativeVisual();
        RequestVisibility(true);
        Assert.That(_provider.HasHighConfidenceCameraFromBoard, Is.False);
        RequestVisibility(true);
        AssertVisibility(true);
        RequestVisibility(false);
        AssertVisibility(false);
        RequestVisibility(true);
        AssertVisibility(true);
    }

    [Test]
    public void BoardPoseRemainsCentered_WhileCanopyReferenceUsesTheOuterTopLeftCorner()
    {
        var board = ReferenceBoardDefinition.Default;
        Assert.That(board.BoardOriginInBoard, Is.EqualTo(Vector3.zero));
        Assert.That(board.WidthMeters, Is.EqualTo(.36f));
        Assert.That(board.HeightMeters, Is.EqualTo(.36f));
        var corner = ReferenceBoardContentDefinition.BoardFromRoofCanopyReference;
        AssertVector(corner.position, new Vector3(-.18f, .18f, 0f));
        Assert.That(corner.rotation, Is.EqualTo(Quaternion.identity));
        AssertVector(board.DjiFiducialMarkers[0].CenterInBoard, new Vector3(-.09f, .09f, 0f));
        Assert.That(Vector3.Distance(corner.position, board.DjiFiducialMarkers[0].CenterInBoard), Is.GreaterThan(.1f));
    }

    [TestCase(0f, 0f, 0f)]
    [TestCase(19f, -31f, 72f)]
    [TestCase(-67f, 143f, -28f)]
    public void RotatedBoard_MapsCornerThroughNativeOpticalAndUnityViewExactlyOnce(float x, float y, float z)
    {
        CreateOverlay();
        var cameraFromBoard = new Pose(new Vector3(.4f, -.2f, 3f), Quaternion.Euler(x, y, z));
        ApplyBoardPose(cameraFromBoard);
        var corner = Field<GameObject>("_cube").transform.parent;
        var camera = Field<Camera>("_overlayCamera");
        var boardRoot = Field<GameObject>("_contentRoot").transform;
        var expectedNative = cameraFromBoard.position +
                             cameraFromBoard.rotation * new Vector3(-.18f, .18f, 0f);
        var expectedView = DjiSharedReferenceCubeOverlay.NativeOpticalToUnityViewBasis.MultiplyPoint3x4(expectedNative);
        AssertVector(camera.worldToCameraMatrix.MultiplyPoint3x4(corner.position), expectedView);
        AssertVector(boardRoot.localPosition, Vector3.zero);
        Assert.That(boardRoot.rotation, Is.EqualTo(Quaternion.identity));
        AssertVector(corner.localPosition, new Vector3(-.18f, .18f, 0f));
        Assert.That(camera.worldToCameraMatrix, Is.EqualTo(
            DjiSharedReferenceCubeOverlay.NativeOpticalToUnityViewBasis *
            ReferenceFrameTransforms.MatrixFromPose(cameraFromBoard)));
    }

    [Test]
    public void CanopyCornerReference_PreservesOriginalPlacementModelAndMaterials()
    {
        CreateOverlay();
        var placement = Field<GameObject>("_cube").transform;
        var corner = placement.parent;
        var board = Field<GameObject>("_contentRoot").transform;
        var model = placement.Find("BoardRoofCanopy");
        var oldReference = new GameObject("Previous centered placement reference");
        try
        {
            var oldPlacement = ARContentFactory.Create(ARContentType.RoofCanopy, oldReference.transform, 2).transform;
            var oldModel = oldPlacement.Find("BoardRoofCanopy");
            AssertVector(oldReference.transform.position, ReferenceBoardDefinition.Default.BoardOriginInBoard);
            Assert.That(corner.parent, Is.SameAs(board));
            AssertVector(placement.localPosition, new Vector3(-.099755391f, -.014564678f, -.086760163f));
            AssertVector(placement.localPosition, oldPlacement.localPosition);
            Assert.That(placement.localRotation, Is.EqualTo(oldPlacement.localRotation));
            AssertVector(placement.localScale, Vector3.one);
            AssertVector(corner.localScale, Vector3.one);
            AssertVector(model.localPosition, Vector3.zero);
            AssertVector(model.localScale, oldModel.localScale);
            Assert.That(model.localRotation, Is.EqualTo(oldModel.localRotation));
            foreach (Transform previous in oldModel)
            {
                var actual = model.Find(previous.name);
                AssertVector(actual.position - previous.position, new Vector3(-.18f, .18f, 0f));
                Assert.That(actual.rotation, Is.EqualTo(previous.rotation));
                AssertVector(actual.lossyScale, previous.lossyScale);
                Assert.That(actual.GetComponent<MeshFilter>().sharedMesh, Is.SameAs(previous.GetComponent<MeshFilter>().sharedMesh));
                CollectionAssert.AreEqual(previous.GetComponent<Renderer>().sharedMaterials,
                    actual.GetComponent<Renderer>().sharedMaterials);
            }
            var semanticAnchor = new Vector3(.099755391f, .014564678f, .086760163f);
            AssertVector(placement.TransformPoint(semanticAnchor), corner.position);
        }
        finally { UnityEngine.Object.DestroyImmediate(oldReference); }
    }

    [Test]
    public void RepeatedConfigurationAndPoseApplication_DoNotDuplicateCornerOffset()
    {
        CreateOverlay();
        var placement = Field<GameObject>("_cube");
        var reference = placement.transform.parent;
        var board = Field<GameObject>("_contentRoot");
        var shadow = Field<RoofCanopyProjectedShadows>("_projectedShadows");
        for (var i = 0; i < 5; i++)
        {
            _overlay.Configure(_videoObject.GetComponent<Camera>(), _provider);
            ApplyBoardPose(new Pose(new Vector3(i, .2f, 3f), Quaternion.Euler(8, i * 10, -15)));
            Assert.That(Field<GameObject>("_cube"), Is.SameAs(placement));
            Assert.That(placement.transform.parent, Is.SameAs(reference));
            AssertVector(reference.localPosition, new Vector3(-.18f, .18f, 0f));
            Assert.That(board.transform.childCount, Is.EqualTo(2)); // Corner reference and centered board debug geometry.
            Assert.That(reference.GetComponentsInChildren<RoofCanopyProjectedShadows>(true).Length, Is.EqualTo(1));
            Assert.That(shadow.GeometryBuildCount, Is.EqualTo(1));
        }
    }

    [Test]
    public void ProjectedShadowSharesCornerReference_WhileBoardDebugGeometryStaysCentered()
    {
        CreateOverlay();
        var board = Field<GameObject>("_contentRoot").transform;
        var corner = Field<GameObject>("_cube").transform.parent;
        var shadows = Field<RoofCanopyProjectedShadows>("_projectedShadows");
        Assert.That(shadows.transform, Is.SameAs(corner));
        AssertVector(shadows.transform.localPosition, new Vector3(-.18f, .18f, 0f));
        AssertVector(board.Find("DJI Board Frame").localPosition, Vector3.zero);
        Assert.That(shadows.GeometryBuildCount, Is.EqualTo(1));
    }

    [Test]
    public void CubeRemainsBoardCentered_WithoutCanopyReferenceOffset()
    {
        CreateOverlay(ARContentType.Cube);
        var cube = Field<GameObject>("_cube");
        var board = Field<GameObject>("_contentRoot").transform;
        var material = cube.GetComponent<Renderer>().sharedMaterial;
        try
        {
            Assert.That(cube.transform.parent, Is.SameAs(board));
            AssertVector(cube.transform.localPosition, ReferenceBoardContentDefinition.BoardFromDummyCube.position);
            Assert.That(board.Find("RoofCanopy Board Corner Reference"), Is.Null);
        }
        finally { UnityEngine.Object.DestroyImmediate(material); }
    }

    [Test]
    public void PersistentWorldVisualTelemetryAndReacquisition_ShareCornerWithoutAdditionalJump()
    {
        CreateOverlay();
        const long baseline = 100_000_000L;
        var visual = new Pose(new Vector3(.4f, -.3f, 3f), Quaternion.Euler(13f, 29f, -17f));
        var positions = new List<TelemetryAircraftPositionSample>
        {
            new(baseline, 47, 19, 100),
            new(200_000_000L, 47, 19, 100)
        };
        var coordinator = new DjiWorldLocalizationShadowCoordinator();
        Assert.That(coordinator.TryInitializeFromVisualMeasurement(visual, baseline, true, out var reason), Is.True, reason);
        var board = coordinator.Snapshot.worldFromBoard;
        var corner = Field<GameObject>("_cube").transform.parent;
        var camera = Field<Camera>("_overlayCamera");
        Vector3 ApplyResolvedPose()
        {
            Assert.That(WorldLocalizationPoseResolver.TryGetCameraFromBoard(coordinator.Snapshot, out var resolved), Is.True);
            ApplyBoardPose(resolved);
            return camera.worldToCameraMatrix.MultiplyPoint3x4(corner.position);
        }
        var before = ApplyResolvedPose();
        Assert.That(coordinator.Snapshot.phase, Is.EqualTo(WorldLocalizationPhase.VisualLocalized));
        Assert.That(coordinator.TryBeginRotationPropagation(visual, baseline, Quaternion.identity, positions, 300, out reason), Is.True, reason);
        Assert.That(coordinator.TryEvaluateRotationPropagation(200_000_000L, 200_000_000L, Quaternion.identity,
            visual, positions, 300, out reason), Is.True, reason);
        Assert.That(coordinator.Snapshot.phase, Is.EqualTo(WorldLocalizationPhase.Propagating));
        AssertVector(ApplyResolvedPose(), before);
        Assert.That(coordinator.TryUpdateFromVisualMeasurement(visual, 300_000_000L, true, positions, 300, out reason), Is.True, reason);
        Assert.That(coordinator.Snapshot.phase, Is.EqualTo(WorldLocalizationPhase.VisualLocalized));
        AssertVector(ApplyResolvedPose(), before);
        Assert.That(coordinator.Snapshot.worldFromBoard, Is.EqualTo(board));
        AssertVector(corner.localPosition, new Vector3(-.18f, .18f, 0f));
    }

    [Test]
    public void NewProviderSession_RequiresItsOwnFirstAuthoritativeAcquisition()
    {
        CreateOverlay();
        PublishAuthoritativeVisual();
        RequestVisibility(true);
        var newProviderObject = new GameObject("New visibility session");
        try
        {
            var nextProvider = newProviderObject.AddComponent<DjiBoardVisionProvider>();
            _overlay.Configure(_videoObject.GetComponent<Camera>(), nextProvider);
            PublishAuthoritativeVisual();
            RequestVisibility(true);
            AssertVisibility(false);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(newProviderObject);
        }
    }

    [TestCase(ARContentType.RoofCanopy)]
    [TestCase(ARContentType.Cube)]
    public void Fc3582AcceptedVisualPose_InitializesWorldAndRendersWithExistingProjection(ARContentType selection)
    {
        CreateOverlay(selection);
        var world = _providerObject.AddComponent<DjiWorldLocalizationShadow>();
        world.Configure(_provider);
        _provider.ConfigureWorldLocalizationShadow(world);

        RenderFrame();
        AssertVisibility(false);
        Assert.That(world.Snapshot.hasWorld, Is.False);

        for (var i = 1; i <= 3; i++)
            ProcessNativeFrame(i);
        RenderFrame();

        Assert.That(_provider.HasHighConfidenceCameraFromBoard, Is.True);
        Assert.That(world.Snapshot.hasWorld, Is.True);
        AssertVisibility(true);
        Assert.That(_overlay.DisplayedRenderPoseSource, Is.EqualTo(DjiRenderPoseSource.Visual));
        var camera = Field<Camera>("_overlayCamera");
        var expected = DjiSharedReferenceCubeOverlay.NativeOpticalToUnityViewBasis *
                       ReferenceFrameTransforms.MatrixFromPose(_provider.CurrentHighConfidenceCameraFromBoard);
        Assert.That(camera.worldToCameraMatrix, Is.EqualTo(expected));
        var placement = Field<GameObject>("_cube").transform;
        if (selection == ARContentType.RoofCanopy)
            AssertVector(placement.parent.localPosition, new Vector3(-.18f, .18f, 0f));
        else
            Assert.That(placement.parent, Is.SameAs(Field<GameObject>("_contentRoot").transform));
        const float near = 0.01f;
        var previousFrustum = Matrix4x4.Frustum(-960f / 1332.7f * near, 960f / 1332.7f * near,
            -540f / 1332.7f * near, 540f / 1332.7f * near, near, 100f);
        Assert.That(camera.projectionMatrix, Is.EqualTo(previousFrustum));
        Assert.That(DjiFc3582CameraModel.CreateDistortionCoefficients(), Is.EqualTo(new float[5]));
    }

    [TestCase("YUV_420_888_LUMA8", 1920, 1080, 0, "NONE", "NONE", false, true)]
    [TestCase("NV21_LUMA8", 1280, 1080, 0, "NONE", "NONE", false, true)]
    [TestCase("NV21_LUMA8", 1920, 720, 0, "NONE", "NONE", false, true)]
    [TestCase("NV21_LUMA8", 1920, 1080, 90, "NONE", "NONE", false, true)]
    [TestCase("NV21_LUMA8", 1920, 1080, 0, "CENTER", "NONE", false, true)]
    [TestCase("NV21_LUMA8", 1920, 1080, 0, "NONE", "SCALED", false, true)]
    [TestCase("NV21_LUMA8", 1920, 1080, 0, "NONE", "NONE", true, true)]
    [TestCase("NV21_LUMA8", 1920, 1080, 0, "NONE", "NONE", false, false)]
    public void IncompatibleDetectorResult_CannotEstablishAR(string format, int width, int height,
        int rotation, string crop, string resize, bool mirrorX, bool calibrationUsable)
    {
        CreateOverlay();
        for (var i = 1; i <= 3; i++)
            ProcessNativeFrame(i, format, width, height, rotation, crop, resize, mirrorX, calibrationUsable);
        RenderFrame();

        Assert.That(_provider.HasAcceptedCameraFromBoard, Is.False);
        Assert.That(_overlay.HasAuthoritativeVisualReference, Is.False);
        AssertVisibility(false);
    }

    private void ProcessNativeFrame(int sequence, string frameFormat = "NV21_LUMA8",
        int frameWidth = 1920, int frameHeight = 1080, int rotation = 0, string crop = "NONE", string resize = "NONE",
        bool mirrorX = false, bool calibrationUsable = true)
    {
        const float depth = 2f;
        var corners = new float[32];
        var halfSide = DjiFc3582CameraModel.Fx * ReferenceBoardDefinition.AprilTagDetectionSizeMeters / (2f * depth);
        var markers = ReferenceBoardDefinition.Default.DjiFiducialMarkers;
        for (var i = 0; i < markers.Count; i++)
        {
            var position = markers[i].CenterInBoard;
            var x = DjiFc3582CameraModel.Cx + DjiFc3582CameraModel.Fx * position.x / depth;
            var y = DjiFc3582CameraModel.Cy - DjiFc3582CameraModel.Fy * position.y / depth;
            var offset = i * 8;
            corners[offset] = x - halfSide; corners[offset + 1] = y - halfSide;
            corners[offset + 2] = x + halfSide; corners[offset + 3] = y - halfSide;
            corners[offset + 4] = x + halfSide; corners[offset + 5] = y + halfSide;
            corners[offset + 6] = x - halfSide; corners[offset + 7] = y + halfSide;
        }
        var frame = new DjiBoardVisionProvider.NativeBoardResult
        {
            status = 2, calibrationUsable = calibrationUsable, frameSequence = sequence, visionFrameId = sequence,
            visionFrameTimestampNs = sequence * 100_000_000L, detectorFrameFormat = frameFormat,
            frameWidth = frameWidth, frameHeight = frameHeight, frameRotationDegrees = rotation,
            frameCrop = crop, frameResize = resize, frameMirrorX = mirrorX,
            markerCount = 4, cornerCount = 16, correspondenceCount = 16,
            markerIds = new[] { 0, 1, 2, 3 }, markerDecisionMargins = new[] { 80f, 80f, 80f, 80f },
            detectedCornersXY = corners, projectedCornersXY = corners, reprojectionResiduals = new float[16],
            cameraFromBoardPosition = new[] { 0f, 0f, depth },
            cameraFromBoardRotationMatrix = new[] { 1f, 0f, 0f, 0f, -1f, 0f, 0f, 0f, -1f }
        };
        typeof(DjiBoardVisionProvider).GetMethod("ProcessResult", Private)
            .Invoke(_provider, new object[] { frame, frame.visionFrameTimestampNs + 1 });
    }

    private void RenderFrame() => typeof(DjiSharedReferenceCubeOverlay).GetMethod("LateUpdate", Private)
        .Invoke(_overlay, null);

    private void ApplyBoardPose(Pose pose) => typeof(DjiSharedReferenceCubeOverlay)
        .GetMethod("ApplyStandaloneBoardOverlayPose", Private).Invoke(_overlay, new object[] { pose });

    private static void AssertVector(Vector3 actual, Vector3 expected) =>
        Assert.That(Vector3.Distance(actual, expected), Is.LessThan(.00001f), $"actual={actual} expected={expected}");

    private void PublishAuthoritativeVisual()
    {
        var accepted = (Action<Pose, long>)typeof(DjiBoardVisionProvider)
            .GetField("AuthoritativeVisualPoseAccepted", BindingFlags.Instance | BindingFlags.NonPublic)
            .GetValue(_provider);
        accepted?.Invoke(new Pose(Vector3.forward, Quaternion.identity), 100_000_000L);
    }

    private void RequestVisibility(bool visible) => typeof(DjiSharedReferenceCubeOverlay)
        .GetMethod("SetCubeVisible", BindingFlags.Instance | BindingFlags.NonPublic)
        .Invoke(_overlay, new object[] { visible, "TEST_RESOLVED_RENDER_SOURCE" });

    private T Field<T>(string name) => (T)typeof(DjiSharedReferenceCubeOverlay)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_overlay);

    private void AssertVisibility(bool enabled)
    {
        var renderers = Field<Renderer[]>("_contentRenderers");
        Assert.That(renderers.Length, Is.EqualTo(_selection == ARContentType.RoofCanopy ? 3 : 1));
        foreach (var renderer in renderers)
            Assert.That(renderer.enabled, Is.EqualTo(enabled), renderer.name);
    }
}
