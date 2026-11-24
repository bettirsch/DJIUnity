using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

public sealed class RoofCanopyWallShadowTests
{
    private RoofCanopyShadowSettings _settings;
    private readonly Vector3 _ray = new Vector3(.409576f, .573576f, -.709406f).normalized;
    [SetUp] public void Setup()
    {
        _settings = Object.Instantiate(Resources.Load<RoofCanopyShadowSettings>("RoofCanopyShadowSettings"));
        _settings.wallOffset = 0; _settings.wallYawDegrees = 0;
        _settings.wallEnabled = _settings.wallCalibrated = _settings.useFirstSurfaceProjection = true;
        _settings.resolution = 128;
    }
    [TearDown] public void Cleanup() { Object.DestroyImmediate(_settings); }

    [TestCase(0f,-.2f,2f,true)]
    [TestCase(0f,-2f,.2f,false)]
    [TestCase(0f,-1f,.05f,false)]
    [TestCase(0f,0f,1f,true)]
    public void ForwardRayUsesFirstPhysicalSurface(float x, float y, float z, bool wallFirst)
    {
        var p = new Vector3(x,y,z);
        Assert.That(ShadowProjectionMath.TryFirstReceiver(p,_ray,_settings.Ground,_settings.Wall,out var actual,out var hit),Is.True);
        Assert.That(actual,Is.EqualTo(wallFirst));
        Assert.That(hit.z,Is.GreaterThanOrEqualTo(-1e-6f));
        Assert.That(hit.y,Is.LessThanOrEqualTo(1e-6f));
        Assert.That(Vector3.Dot(hit-p,_ray),Is.GreaterThanOrEqualTo(0));
    }
    [Test] public void WallHitBelowGroundBelongsOnlyToGround()
    {
        var p=new Vector3(0,-1,.1f);
        Assert.That(ShadowProjectionMath.TryProject(p,_ray,_settings.Wall,out var wallHit),Is.True);
        Assert.That(wallHit.z,Is.LessThan(0));
        Assert.That(ShadowProjectionMath.TryFirstReceiver(p,_ray,_settings.Ground,_settings.Wall,out var wallFirst,out _),Is.True);
        Assert.That(wallFirst,Is.False);
    }
    [Test] public void ParallelWallRayCanReachGroundButNotWall()
    {
        var ray=new Vector3(.4f,0,-.7f);
        Assert.That(ShadowProjectionMath.TryProject(new Vector3(0,-1,2),ray,_settings.Wall,out _),Is.False);
        Assert.That(ShadowProjectionMath.TryFirstReceiver(new Vector3(0,-1,2),ray,_settings.Ground,_settings.Wall,out var wallFirst,out _),Is.True);
        Assert.That(wallFirst,Is.False);
    }
    [TestCase(.01f)] [TestCase(1f)]
    public void CastersInsideBuildingDoNotProjectThroughWall(float y)
    {
        Assert.That(ShadowProjectionMath.TryFirstReceiver(new Vector3(0,y,1),_ray,_settings.Ground,_settings.Wall,out _,out _),Is.False);
        var box=Box(new Vector3(0,y+.1f,1),new Vector3(.05f,.05f,.05f));
        Assert.That(ShadowProjectionMath.ProjectBoxFirstSurface(box,0,_ray,_settings.Wall,_settings.Ground,_settings.Wall),Is.Empty);
    }
    [Test] public void SimultaneousSeamHitHasSingleGroundOwner()
    {
        var p=-_ray*2;
        Assert.That(ShadowProjectionMath.TryFirstReceiver(p,_ray,_settings.Ground,_settings.Wall,out var wallFirst,out var hit),Is.True);
        Assert.That(wallFirst,Is.False);
        Assert.That(hit.magnitude,Is.LessThan(1e-5f));
    }
    [Test] public void ProjectedPolygonsAreBoundedAndPartitionedAtSeam()
    {
        var box=Box(new Vector3(0,-.4f,.5f),new Vector3(.2f,.3f,.4f));
        foreach(var plane in new[]{_settings.Ground,_settings.Wall})
        {
            var polygon=ShadowProjectionMath.ProjectBoxFirstSurface(box,0,_ray,plane,_settings.Ground,_settings.Wall);
            Assert.That(polygon.Count,Is.GreaterThanOrEqualTo(3));
            foreach(var uv in polygon)
            {
                Assert.That(uv.x,Is.InRange(-1e-6f,1.000001f)); Assert.That(uv.y,Is.InRange(-1e-6f,1.000001f));
                var p=plane.Point(uv);Assert.That(p.z,Is.GreaterThanOrEqualTo(-1e-5f));Assert.That(p.y,Is.LessThanOrEqualTo(1e-5f));
            }
        }
    }
    [Test] public void ReceiverBoundaryClippingRejectsFarOutsideProjection()
    {
        var box=Box(new Vector3(100,-.1f,1),Vector3.one*.05f);
        Assert.That(ShadowProjectionMath.ProjectBoxFirstSurface(box,0,_ray,_settings.Wall,_settings.Ground,_settings.Wall),Is.Empty);
    }
    [Test] public void GroundMaskAndActualMeshCannotLeakBehindWall()
    {
        var bytes=ShadowProjectionMath.Bake(_settings,_settings.Ground,out int w,out int h);
        for(int y=0;y<h;y++)for(int x=0;x<w;x++)
            if(_settings.Ground.Point(new Vector2((x+.5f)/w,(y+.5f)/h)).y>0)Assert.That(bytes[y*w+x],Is.Zero);
        var board=new GameObject("Clipped receiver test");
        try
        {
            var s=RoofCanopyProjectedShadows.Create(board.transform,2,_settings);s.SetVisible(true);
            var ground=board.GetComponentsInChildren<MeshFilter>().Single(f=>f.name.Contains("Ground"));
            foreach(var p in ground.sharedMesh.vertices)Assert.That(p.y,Is.LessThanOrEqualTo(1e-6f));
        } finally {Object.DestroyImmediate(board);}
    }
    [Test] public void MaskOpacityIsNotDoubledByOverlappingCasters()
    {
        var a=ShadowProjectionMath.Bake(_settings,_settings.Wall,out _,out _);
        Assert.That(a.Any(v=>v>0),Is.True);
        _settings.woodProxyCorners=_settings.woodProxyCorners.Concat(_settings.woodProxyCorners).ToArray();
        var b=ShadowProjectionMath.Bake(_settings,_settings.Wall,out _,out _);
        Assert.That(b,Is.EqualTo(a));Assert.That(b.Max(),Is.LessThanOrEqualTo(Mathf.CeilToInt(_settings.opacity*255)));
    }
    [Test] public void CameraAndVisibilityDoNotRebakeOrMoveEitherReceiver()
    {
        var board=new GameObject("Fixed wall receiver");var camera=new GameObject("Unrelated camera",typeof(Camera));
        try
        {
            var s=RoofCanopyProjectedShadows.Create(board.transform,2,_settings);
            var filters=board.GetComponentsInChildren<MeshFilter>(true);
            var before=filters.SelectMany(f=>f.sharedMesh.vertices.Select(f.transform.TransformPoint)).ToArray();
            for(int i=0;i<100;i++){camera.transform.SetPositionAndRotation(new Vector3(i,2,3),Quaternion.Euler(i,2*i,3*i));s.SetVisible((i&1)==0);}
            Assert.That(filters.SelectMany(f=>f.sharedMesh.vertices.Select(f.transform.TransformPoint)).ToArray(),Is.EqualTo(before));
            Assert.That(s.GeometryBuildCount,Is.EqualTo(1));
        }finally{Object.DestroyImmediate(board);Object.DestroyImmediate(camera);}
    }
    [Test] public void ExplicitConfigurationRebuildReplacesOldMasksAndKeepsResourceCountBounded()
    {
        var board=new GameObject("Rebake wall receiver");
        try
        {
            var s=RoofCanopyProjectedShadows.Create(board.transform,2,_settings);
            s.SetVisible(true);
            var old=board.GetComponentsInChildren<Renderer>(true).Select(r=>r.sharedMaterial.GetTexture("_ShadowMask")).ToArray();
            _settings.sunlightRayDirection=new Vector3(-_ray.x,_ray.y,_ray.z);
            Assert.That(s.RebuildExperiment(_settings,2),Is.True);
            Assert.That(old.All(t=>t==null),Is.True);
            Assert.That(s.GeometryBuildCount,Is.EqualTo(2));
            Assert.That(s.GroundVisible&&s.WallVisible,Is.True);
            Assert.That(board.GetComponentsInChildren<MeshFilter>(true).Length,Is.EqualTo(2));
            _settings.wallOffset=.01f;Assert.That(s.RebuildExperiment(_settings,2),Is.True);
            Assert.That(board.GetComponentsInChildren<MeshFilter>(true).Length,Is.EqualTo(2));
        } finally {Object.DestroyImmediate(board);}
    }
    [Test] public void NormalBuildStaysDisabledAndCannotMutateSourceSettings()
    {
        Assert.That(RoofCanopyWallShadowExperiment.Enabled,Is.False);
        var source=Resources.Load<RoofCanopyShadowSettings>("RoofCanopyShadowSettings");
        var geometry=ScriptableObject.CreateInstance<RoofCanopyWallShadowExperiment>();
        try
        {
            geometry.geometryVerified=true;geometry.sourceFbxSha256=source.sourceFbxSha256;geometry.wallOffset=-.003f;
            Assert.That(RoofCanopyWallShadowExperiment.TryCreateSettings(source,geometry,
                RoofCanopyWallShadowExperiment.Enabled,out var none),Is.False);
            Assert.That(none,Is.Null);
            Assert.That(RoofCanopyWallShadowExperiment.TryCreateSettings(source,geometry,true,out var clone),Is.True);
            try
            {
                Assert.That(clone,Is.Not.SameAs(source));Assert.That(clone.useFirstSurfaceProjection&&clone.wallEnabled&&clone.wallCalibrated,Is.True);
                Assert.That(clone.sunlightRayDirection,Is.EqualTo(source.sunlightRayDirection));
                Assert.That(source.wallEnabled||source.wallCalibrated||source.useFirstSurfaceProjection,Is.False);
                Assert.That(source.wallOffset,Is.EqualTo(.25f));
            }finally{Object.DestroyImmediate(clone);}
        }finally{Object.DestroyImmediate(geometry);}
    }
    [Test] public void UnverifiedOrStaleGeometryFailsClosed()
    {
        var geometry=ScriptableObject.CreateInstance<RoofCanopyWallShadowExperiment>();
        try
        {
            Assert.That(RoofCanopyWallShadowExperiment.TryCreateSettings(_settings,geometry,true,out _),Is.False);
            geometry.geometryVerified=true;geometry.sourceFbxSha256="wrong source";
            Assert.That(RoofCanopyWallShadowExperiment.TryCreateSettings(_settings,geometry,true,out _),Is.False);
        }finally{Object.DestroyImmediate(geometry);}
    }
    [Test] public void NormalGroundMaskIsByteIdenticalToValidatedFixedC()
    {
        var source=Resources.Load<RoofCanopyShadowSettings>("RoofCanopyShadowSettings");
        var bytes=ShadowProjectionMath.Bake(source,source.Ground,out _,out _);
        using(var hash=System.Security.Cryptography.SHA256.Create())
            Assert.That(BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-",""),
                Is.EqualTo("BFDE4C7A15397F81A865120A451DBC139E2896FEA388746045A1CE0168B4D58D"));
    }
    [Test] public void ExperimentAssetUsesGeometryDerivedRafterSupportAndMatchingSource()
    {
        var geometry=Resources.Load<RoofCanopyWallShadowExperiment>("RoofCanopyWallShadowExperiment");
        var source=Resources.Load<RoofCanopyShadowSettings>("RoofCanopyShadowSettings");
        Assert.That(geometry,Is.Not.Null);Assert.That(geometry.geometryVerified,Is.True);
        Assert.That(geometry.wallOffset,Is.EqualTo(-.0036760657f).Within(1e-7f));
        Assert.That(geometry.sourceFbxSha256,Is.EqualTo(source.sourceFbxSha256));
        Assert.That(RoofCanopyWallShadowExperiment.TryCreateSettings(source,geometry,true,out var clone),Is.True);
        try
        {
            Assert.That(clone.Wall.normal,Is.EqualTo(Vector3.down));
            Assert.That(clone.Wall.Point(Vector2.zero).z,Is.Zero.Within(1e-6));
            Assert.That(clone.Wall.center.y,Is.EqualTo(geometry.wallOffset));
        }finally{Object.DestroyImmediate(clone);}
    }
    [Test] public void WallVisibilitySyncAllocatesNothingAndDoesNotRebake()
    {
        var board=new GameObject("Wall allocation test");
        try
        {
            var s=RoofCanopyProjectedShadows.Create(board.transform,2,_settings);s.SetVisible(true);s.SetVisible(false);
            long before=GC.GetAllocatedBytesForCurrentThread();
            for(int i=0;i<10000;i++)s.SetVisible((i&1)==0);
            Assert.That(GC.GetAllocatedBytesForCurrentThread()-before,Is.Zero);
            Assert.That(s.GeometryBuildCount,Is.EqualTo(1));
            s.enabled=false;Assert.That(s.GroundVisible||s.WallVisible,Is.False);
        }finally{Object.DestroyImmediate(board);}
    }
    private static Vector3[] Box(Vector3 center,Vector3 half)
    {
        var points=new Vector3[8];for(int i=0;i<8;i++)points[i]=center+new Vector3((i&1)==0?-half.x:half.x,(i&2)==0?-half.y:half.y,(i&4)==0?-half.z:half.z);return points;
    }
}
