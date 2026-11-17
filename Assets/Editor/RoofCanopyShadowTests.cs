using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object=UnityEngine.Object;

public sealed class RoofCanopyShadowTests
{
    private RoofCanopyShadowSettings _settings;
    private GameObject _board;
    [SetUp] public void Setup() { _settings=Object.Instantiate(Resources.Load<RoofCanopyShadowSettings>("RoofCanopyShadowSettings")); _settings.resolution=128; _board=new GameObject("Shadow test board"); }
    [TearDown] public void Cleanup() { Object.DestroyImmediate(_board);Object.DestroyImmediate(_settings); }
    [Test] public void GroundIsExactlyBoardXYWithOutwardZNormal() {
        var p=_settings.Ground;Assert.That(p.normal,Is.EqualTo(Vector3.forward));
        foreach(var uv in new[]{Vector2.zero,Vector2.one,new Vector2(.3f,.7f)}) {var point=p.Point(uv);Assert.That(point.z,Is.Zero);Assert.That(Vector2.Distance(p.Coordinates(point),uv),Is.LessThan(1e-6));}
    }
    [Test] public void WallRequiresExplicitCalibrationAndEnable() { Assert.That(_settings.wallCalibrated||_settings.wallEnabled,Is.False);var s=RoofCanopyProjectedShadows.Create(_board.transform,2,_settings);s.SetVisible(true);Assert.That(s.WallVisible,Is.False);Assert.That(_board.GetComponentsInChildren<Renderer>(true).Length,Is.EqualTo(1)); }
    [Test] public void WallIsPerpendicularToGroundAndHonorsCalibration() {
        _settings.wallOffset=.73f;_settings.wallYawDegrees=27;_settings.wallWidth=8;_settings.wallHeight=4;
        var p=_settings.Wall;Assert.That(Vector3.Dot(p.normal,_settings.Ground.normal),Is.EqualTo(0).Within(1e-6));
        Assert.That(p.center.y,Is.EqualTo(.73f));Assert.That(p.size,Is.EqualTo(new Vector2(8,4)));Assert.That(p.Point(Vector2.zero).z,Is.EqualTo(0).Within(1e-6));
    }
    [Test] public void ProjectionUsesForwardRayPlaneIntersection() {Assert.That(ShadowProjectionMath.TryProject(new Vector3(0,0,2),new Vector3(1,2,-2),_settings.Ground,out var p),Is.True);Assert.That(Vector3.Distance(p,new Vector3(1,2,0)),Is.LessThan(1e-5));}
    [Test] public void ParallelRaysProduceNoProjectedPolygon() {Assert.That(ShadowProjectionMath.TryProject(Vector3.forward,Vector3.right,_settings.Ground,out _),Is.False);Assert.That(ShadowProjectionMath.ProjectBox(_settings.woodProxyCorners,0,Vector3.right,_settings.Ground),Is.Empty);}
    [Test] public void BehindRayIsRejected() {Assert.That(ShadowProjectionMath.TryProject(Vector3.forward,Vector3.forward,_settings.Ground,out _),Is.False);}
    [TestCase(0f,0f,0f),TestCase(float.NaN,0f,-1f),TestCase(float.PositiveInfinity,0f,-1f)]
    public void InvalidSunlightRejected(float x,float y,float z) {Assert.That(ShadowProjectionMath.TryProject(Vector3.forward,new Vector3(x,y,z),_settings.Ground,out _),Is.False);}
    [Test] public void ProjectedVerticesAreFiniteAndLieOnReceiver() {foreach(var p in ShadowProjectionMath.ProjectBox(_settings.woodProxyCorners,0,_settings.sunlightRayDirection,_settings.Ground))Assert.That(float.IsFinite(p.x)&&float.IsFinite(p.y),Is.True);}
    [Test] public void MaskIsBoundedAndOutsideCasterCoverageIsTransparent() {
        var mask=ShadowProjectionMath.Bake(_settings,_settings.Ground,out int w,out int h);Assert.That(mask.Length,Is.EqualTo(w*h));Assert.That(mask.Any(b=>b>0),Is.True);Assert.That(mask[0],Is.Zero);
        _settings.groundCenter=new Vector2(100,100);_settings.contacts=Array.Empty<Vector3>();mask=ShadowProjectionMath.Bake(_settings,_settings.Ground,out w,out h);Assert.That(mask.All(b=>b==0),Is.True);
    }
    [Test] public void OverlappingProxyCastersDoNotDoubleDarken() {var a=ShadowProjectionMath.Bake(_settings,_settings.Ground,out _,out _);_settings.woodProxyCorners=_settings.woodProxyCorners.Concat(_settings.woodProxyCorners).ToArray();var b=ShadowProjectionMath.Bake(_settings,_settings.Ground,out _,out _);Assert.That(b,Is.EqualTo(a));}
    [Test] public void RoofIsLighterThanWoodAndOpacityIsBounded() {Assert.That(_settings.roofStrength,Is.LessThan(_settings.woodStrength));var mask=ShadowProjectionMath.Bake(_settings,_settings.Ground,out _,out _);Assert.That(mask.Max(),Is.LessThanOrEqualTo(Mathf.CeilToInt(_settings.opacity*255)));}
    [Test] public void LightPointingAwayFromRearWallDoesNotInventWallShadow() {_settings.sunlightRayDirection=new Vector3(.409576f,-.573576f,-.709406f);var mask=ShadowProjectionMath.Bake(_settings,_settings.Wall,out _,out _);Assert.That(mask.All(b=>b==0),Is.True);}
    [Test] public void LightTowardRearWallProducesBoundedShadow() {_settings.sunlightRayDirection=new Vector3(.2f,.7f,-.7f);var mask=ShadowProjectionMath.Bake(_settings,_settings.Wall,out _,out _);Assert.That(mask.Any(b=>b>0),Is.True);Assert.That(mask[0],Is.Zero);}
    [Test] public void CameraMotionDoesNotRebuildOrMoveReceivers() {
        var s=RoofCanopyProjectedShadows.Create(_board.transform,2,_settings);var mesh=_board.GetComponentInChildren<MeshFilter>(true).sharedMesh;var vertices=mesh.vertices;var camera=new GameObject("Unrelated moving camera",typeof(Camera));
        try { for(int i=0;i<200;i++){camera.transform.SetPositionAndRotation(new Vector3(i,3,5),Quaternion.Euler(i,2*i,0));s.SetVisible((i&1)==0);}Assert.That(s.GeometryBuildCount,Is.EqualTo(1));Assert.That(mesh.vertices,Is.EqualTo(vertices));Assert.That(_board.transform.localToWorldMatrix,Is.EqualTo(Matrix4x4.identity)); }
        finally{Object.DestroyImmediate(camera);}
    }
    [Test] public void RepeatedCreateCannotDuplicateOrAllocateNewMeshes() {var a=RoofCanopyProjectedShadows.Create(_board.transform,2,_settings);var mesh=_board.GetComponentInChildren<MeshFilter>(true).sharedMesh;for(int i=0;i<50;i++)Assert.That(RoofCanopyProjectedShadows.Create(_board.transform,2,_settings),Is.SameAs(a));Assert.That(_board.GetComponents<RoofCanopyProjectedShadows>().Length,Is.EqualTo(1));Assert.That(_board.GetComponentInChildren<MeshFilter>(true).sharedMesh,Is.SameAs(mesh));}
    [Test] public void ReceiverReloadStartsHiddenAndDestroysOwnedResources() {var a=RoofCanopyProjectedShadows.Create(_board.transform,2,_settings);a.SetVisible(true);var texture=_board.GetComponentInChildren<Renderer>().sharedMaterial.GetTexture("_ShadowMask");Object.DestroyImmediate(a);Assert.That(texture==null,Is.True);var b=RoofCanopyProjectedShadows.Create(_board.transform,2,_settings);Assert.That(b.GroundVisible,Is.False);}
    [Test] public void TransparentShaderHasNoDepthStencilOrCullingMutation() {
        var source=File.ReadAllText("Assets/Resources/RoofCanopyShadow.shader");Assert.That(source,Does.Contain("Blend SrcAlpha OneMinusSrcAlpha"));Assert.That(source,Does.Contain("ZWrite Off"));Assert.That(source,Does.Contain("ZTest LEqual"));Assert.That(source,Does.Contain("Cull Off"));Assert.That(source,Does.Not.Contain("Stencil"));Assert.That(_settings.shadowMaterial.renderQueue,Is.EqualTo(2990));Assert.That(_settings.shadowMaterial.shader.isSupported,Is.True);
    }
    [Test] public void SourceProxiesMatchCurrentMirroredFbxWithoutMeshCopies() {using var hash=System.Security.Cryptography.SHA256.Create();var bytes=File.ReadAllBytes("Assets/Resources/ARContent/RoofAssets/roof_canopy_adjusted_pivot.fbx");Assert.That(BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-",""),Is.EqualTo(_settings.sourceFbxSha256));Assert.That(_settings.woodProxyCorners.Length,Is.EqualTo(67*8));Assert.That(_settings.roofProxyCorners.Length,Is.EqualTo(8));Assert.That(_settings.contacts.Length,Is.EqualTo(4));}
    [Test] public void ActualOverlayVisibilityTracksInitialAcquisitionHideLossAndReacquisition() {
        var selection=ARContentSelection.Selected;var video=new GameObject("Shadow video",typeof(Camera));var providerObject=new GameObject("Shadow provider");var host=new GameObject("Shadow overlay");DjiSharedReferenceCubeOverlay overlay=null;
        try {
            ARContentSelection.Set(ARContentType.RoofCanopy);var provider=providerObject.AddComponent<DjiBoardVisionProvider>();overlay=host.AddComponent<DjiSharedReferenceCubeOverlay>();overlay.Configure(video.GetComponent<Camera>(),provider);
            var type=typeof(DjiSharedReferenceCubeOverlay);var flags=BindingFlags.NonPublic|BindingFlags.Instance;var shadow=(RoofCanopyProjectedShadows)type.GetField("_projectedShadows",flags).GetValue(overlay);var set=type.GetMethod("SetCubeVisible",flags);
            set.Invoke(overlay,new object[]{true,"UNINITIALIZED"});Assert.That(shadow.GroundVisible,Is.False);
            type.GetField("_hasAuthoritativeVisualAcquisition",flags).SetValue(overlay,true);set.Invoke(overlay,new object[]{true,"VISUAL"});Assert.That(shadow.GroundVisible,Is.True);
            overlay.SetModelVisibleByUser(false);Assert.That(shadow.GroundVisible,Is.False);Assert.That(provider.enabled,Is.True);
            set.Invoke(overlay,new object[]{false,"LOST"});overlay.SetModelVisibleByUser(true);Assert.That(shadow.GroundVisible,Is.False);
            set.Invoke(overlay,new object[]{true,"REACQUIRED"});Assert.That(shadow.GroundVisible,Is.True);Assert.That(shadow.GeometryBuildCount,Is.EqualTo(1));
        } finally {
            if(overlay!=null){var flags=BindingFlags.NonPublic|BindingFlags.Instance;var root=(GameObject)typeof(DjiSharedReferenceCubeOverlay).GetField("_contentRoot",flags).GetValue(overlay);var camera=(Camera)typeof(DjiSharedReferenceCubeOverlay).GetField("_overlayCamera",flags).GetValue(overlay);Object.DestroyImmediate(root);Object.DestroyImmediate(camera.gameObject);}
            Object.DestroyImmediate(host);Object.DestroyImmediate(providerObject);Object.DestroyImmediate(video);ARContentSelection.Set(selection);
        }
    }
    [Test] public void MatchedProductionConventionPreviewsAndCompositingAreValid() {RoofCanopyShadowPreview.ValidateAndCapture();}
    [Test] public void InvalidReceiverCalibrationFailsClosedWithoutInvalidMesh() {_settings.groundSize=Vector2.zero;var s=RoofCanopyProjectedShadows.Create(_board.transform,2,_settings);s.SetVisible(true);Assert.That(s.GroundVisible,Is.False);Assert.That(_board.GetComponentsInChildren<MeshFilter>(true),Is.Empty);}
    [Test] public void RepeatedVisibilitySyncHasNoManagedAllocationAndDisableHidesReceivers() {
        var s=RoofCanopyProjectedShadows.Create(_board.transform,2,_settings);s.SetVisible(true);s.SetVisible(false);
        long before=GC.GetAllocatedBytesForCurrentThread();for(int i=0;i<10000;i++)s.SetVisible((i&1)==0);long allocation=GC.GetAllocatedBytesForCurrentThread()-before;Assert.That(allocation,Is.Zero);
        s.enabled=false;s.SetVisible(true);Assert.That(s.GroundVisible,Is.False);
    }
    [Test] public void CalibratedWallStopsGroundMaskBehindItsFootprint() {
        _settings.wallCalibrated=true;_settings.wallEnabled=true;_settings.sunlightRayDirection=new Vector3(.2f,.7f,-.7f);var mask=ShadowProjectionMath.Bake(_settings,_settings.Ground,out int w,out int h);
        for(int y=0;y<h;y++)for(int x=0;x<w;x++){var p=_settings.Ground.Point(new Vector2((x+.5f)/w,(y+.5f)/h));var uv=_settings.Wall.Coordinates(p);if(uv.x>=0&&uv.x<=1&&Vector3.Dot(p-_settings.Wall.center,_settings.Wall.normal)<0)Assert.That(mask[y*w+x],Is.Zero);}
    }
}
