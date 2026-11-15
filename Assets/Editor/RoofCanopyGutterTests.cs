using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object=UnityEngine.Object;

internal sealed class GutterMeshFixture
{
    internal readonly MeshFilter Filter;
    internal readonly Vector3[] Points,Normals;
    internal readonly int[] Triangles,Channel;
    internal readonly int[] Welded;
    internal readonly Bounds Bounds;
    internal GutterMeshFixture(GameObject content)
    {
        Filter=content.GetComponentsInChildren<MeshFilter>().Single(f=>f.name=="drainage");var mesh=Filter.sharedMesh;
        Points=mesh.vertices.Select(Filter.transform.TransformPoint).ToArray();var n=Filter.transform.localToWorldMatrix.inverse.transpose;Normals=mesh.normals.Select(v=>n.MultiplyVector(v).normalized).ToArray();Triangles=mesh.triangles;
        var keys=new Dictionary<Vector3Int,int>();Welded=new int[Points.Length];
        for(int i=0;i<Points.Length;i++){var p=Points[i];var k=new Vector3Int(Mathf.RoundToInt(p.x*1000000),Mathf.RoundToInt(p.y*1000000),Mathf.RoundToInt(p.z*1000000));if(!keys.TryGetValue(k,out int id)){id=keys.Count;keys.Add(k,id);}Welded[i]=id;}
        var parent=Enumerable.Range(0,keys.Count).ToArray();int Root(int i){while(parent[i]!=i){parent[i]=parent[parent[i]];i=parent[i];}return i;}
        for(int i=0;i<Triangles.Length;i+=3){int r=Root(Welded[Triangles[i]]);parent[Root(Welded[Triangles[i+1]])]=r;parent[Root(Welded[Triangles[i+2]])]=r;}
        var groups=Enumerable.Range(0,Triangles.Length/3).GroupBy(t=>Root(Welded[Triangles[t*3]])).Select(g=>g.ToArray()).ToArray();
        Channel=groups.Single(g=>{var b=BoundsOf(g.SelectMany(t=>Enumerable.Range(0,3).Select(i=>Points[Triangles[t*3+i]])));return b.size.x>4.9f&&b.min.y<-1.64f&&b.max.y<-1.51f&&b.min.z>2.09f&&b.max.z<2.17f;});
        Bounds=BoundsOf(Channel.SelectMany(t=>Enumerable.Range(0,3).Select(i=>Points[Triangles[t*3+i]])));
    }
    internal Vector3 Center(int triangle)=>(Points[Triangles[triangle*3]]+Points[Triangles[triangle*3+1]]+Points[Triangles[triangle*3+2]])/3;
    internal Vector3 Normal(int triangle)=>(Normals[Triangles[triangle*3]]+Normals[Triangles[triangle*3+1]]+Normals[Triangles[triangle*3+2]]).normalized;
    internal static Bounds BoundsOf(IEnumerable<Vector3> points){var p=points.ToArray();var b=new Bounds(p[0],Vector3.zero);foreach(var v in p)b.Encapsulate(v);return b;}
}

public sealed class RoofCanopyGutterGeometryTests
{
    GameObject content;GutterMeshFixture gutter;
    [SetUp]public void SetUp(){content=RoofCanopyLightingPreview.CreateContent();gutter=new GutterMeshFixture(content);}
    [TearDown]public void TearDown()=>Object.DestroyImmediate(content);

    [Test]public void ChannelShell_IsClosedWithOppositeDirectedEdges()
    {
        var edges=new Dictionary<(int,int),int>();foreach(int t in gutter.Channel)for(int i=0;i<3;i++){int a=gutter.Welded[gutter.Triangles[t*3+i]],b=gutter.Welded[gutter.Triangles[t*3+(i+1)%3]];var key=(a,b);edges.TryGetValue(key,out int count);edges[key]=count+1;}
        foreach(var e in edges){Assert.That(e.Value,Is.EqualTo(1));Assert.That(edges.TryGetValue((e.Key.Item2,e.Key.Item1),out int n)&&n==1,Is.True);}
    }
    [Test]public void InnerAndOuterChannelNormals_FaceTheirRespectiveAirVolumes()
    {
        int inside=0,outside=0;var center=new Vector3(0,(gutter.Bounds.min.y+gutter.Bounds.max.y)/2,gutter.Bounds.max.z);
        float outerRadius=gutter.Bounds.size.y/2;
        foreach(int t in gutter.Channel){var p=gutter.Center(t);var n=gutter.Normal(t);if(Mathf.Abs(n.x)>.1f||p.z>center.z-.005f)continue;var radial=new Vector3(0,p.y-center.y,p.z-center.z);float dot=Vector3.Dot(n,radial.normalized);
            // The inner contour is not concentric: classify the actual outer-radius vertices,
            // not a fixed centroid radius that crosses the thin inner floor.
            var radii=Enumerable.Range(0,3).Select(i=>{var v=gutter.Points[gutter.Triangles[t*3+i]];return new Vector2(v.y-center.y,v.z-center.z).magnitude;}).ToArray();
            if(radii.All(r=>Mathf.Abs(r-outerRadius)<.000005f)){Assert.That(dot,Is.GreaterThan(.98f));outside++;}else{Assert.That(radii.All(r=>r<outerRadius-.00005f),Is.True);Assert.That(dot,Is.LessThan(-.98f));inside++;}}
        Assert.That(inside,Is.GreaterThanOrEqualTo(20));Assert.That(outside,Is.GreaterThanOrEqualTo(20));
    }
    [Test]public void UpperChannelOpening_IsNotCapped_AndRetainsMeasuredCrossSection()
    {
        Assert.That(gutter.Bounds.size.x,Is.EqualTo(4.972707f).Within(.00001f));Assert.That(gutter.Bounds.size.y,Is.EqualTo(.1268412f).Within(.000005f));Assert.That(gutter.Bounds.size.z,Is.EqualTo(.0634208f).Within(.000005f));
        var origin=new Vector3(1,(gutter.Bounds.min.y+gutter.Bounds.max.y)/2,10);var direction=Vector3.back;var hits=new List<float>();
        foreach(int t in gutter.Channel){var a=gutter.Points[gutter.Triangles[t*3]];var e1=gutter.Points[gutter.Triangles[t*3+1]]-a;var e2=gutter.Points[gutter.Triangles[t*3+2]]-a;var p=Vector3.Cross(direction,e2);float det=Vector3.Dot(e1,p);if(Mathf.Abs(det)<1e-10f)continue;var s=origin-a;float u=Vector3.Dot(s,p)/det;if(u<0||u>1)continue;var q=Vector3.Cross(s,e1);float v=Vector3.Dot(direction,q)/det;if(v<0||u+v>1)continue;float distance=Vector3.Dot(e2,q)/det;if(distance>0)hits.Add(origin.z-distance);}
        Assert.That(hits.Count,Is.GreaterThan(0));Assert.That(hits.Max(),Is.LessThan(gutter.Bounds.max.z-.04f));Assert.That(hits.Max(),Is.GreaterThan(gutter.Bounds.min.z));
    }
    [Test]public void RoofEdgeRemainsAboveChannel_WithoutPlacementOrMaterialWorkaround()
    {
        var roof=content.GetComponentsInChildren<MeshFilter>().Single(f=>f.name=="roof");var b=GutterMeshFixture.BoundsOf(roof.sharedMesh.vertices.Select(roof.transform.TransformPoint));
        Assert.That(b.min.z-gutter.Bounds.max.z,Is.InRange(.011f,.013f));
        var renderer=gutter.Filter.GetComponent<MeshRenderer>();Assert.That(renderer.sharedMaterial.name,Is.EqualTo("Aluminium scratches"));Assert.That(renderer.sharedMaterial.GetFloat("_Cull"),Is.EqualTo(2));Assert.That(renderer.sharedMaterial.GetFloat("_Metallic"),Is.EqualTo(.8f));Assert.That(renderer.sharedMaterial.GetFloat("_Smoothness"),Is.EqualTo(.5f));
        Assert.That(content.GetComponentsInChildren<MeshFilter>().Any(f=>f.sharedMesh.name=="Cube"),Is.False);
    }
}

public sealed class RoofCanopyGutterVisibilityTests
{
    Scene previous,scene;GameObject content;Camera baseCamera,overlay;Material material;Mesh selected;DJIBackgroundURPFeature background;bool active;int quality;
    [SetUp]public void SetUp()
    {
        previous=SceneManager.GetActiveScene();scene=EditorSceneManager.OpenScene("Assets/Scenes/DroneView.unity",OpenSceneMode.Additive);SceneManager.SetActiveScene(scene);quality=QualitySettings.GetQualityLevel();QualitySettings.SetQualityLevel(0,false);
        var data=UnityEditor.AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/Settings/Mobile_Renderer.asset");Assert.That(data.rendererFeatures.OfType<DjiOverlayCullingParityFeature>().Count(),Is.EqualTo(1));background=data.rendererFeatures.OfType<DJIBackgroundURPFeature>().Single();active=background.isActive;background.SetActive(false);
        content=RoofCanopyLightingPreview.CreateContent();baseCamera=Camera("Gutter test Base");baseCamera.cullingMask=0;overlay=Camera("Gutter test Overlay");overlay.clearFlags=CameraClearFlags.Depth;overlay.GetUniversalAdditionalCameraData().renderType=CameraRenderType.Overlay;overlay.gameObject.AddComponent<DjiOverlayCullingParity>();baseCamera.GetUniversalAdditionalCameraData().cameraStack.Add(overlay);overlay.enabled=true;
        material=new Material(Shader.Find("Universal Render Pipeline/Unlit"));material.SetColor("_BaseColor",Color.red);material.SetFloat("_Cull",2);
    }
    Camera Camera(string name){var c=new GameObject(name,typeof(Camera),typeof(UniversalAdditionalCameraData)).GetComponent<Camera>();c.enabled=false;c.cullingMask=1<<2;c.clearFlags=CameraClearFlags.SolidColor;c.backgroundColor=Color.black;c.nearClipPlane=.01f;c.farClipPlane=100;c.allowHDR=false;c.allowMSAA=false;return c;}
    [TearDown]public void TearDown(){if(background!=null)background.SetActive(active);if(content!=null)Object.DestroyImmediate(content);if(baseCamera!=null)Object.DestroyImmediate(baseCamera.gameObject);if(overlay!=null)Object.DestroyImmediate(overlay.gameObject);if(material!=null)Object.DestroyImmediate(material);if(selected!=null)Object.DestroyImmediate(selected);if(previous.IsValid()&&previous.isLoaded)SceneManager.SetActiveScene(previous);if(scene.IsValid()&&scene.isLoaded)EditorSceneManager.CloseScene(scene,true);QualitySettings.SetQualityLevel(quality,false);}

    [TestCase(true,true),TestCase(true,false),TestCase(false,true),TestCase(false,false)]
    public void CorrectedOverlay_OneSidedInteriorAndExteriorVisibility(bool interior,bool above)
    {
        var f=new GutterMeshFixture(content);var renderer=f.Filter.GetComponent<MeshRenderer>();foreach(var r in content.GetComponentsInChildren<MeshRenderer>())r.enabled=r==renderer;
        var triangles=f.Channel.Where(t=>Mathf.Abs(f.Normal(t).x)<.1f&&f.Center(t).z<f.Bounds.max.z-.005f&&(interior?f.Normal(t).z>.3f:f.Normal(t).z<-.3f)).SelectMany(t=>f.Triangles.Skip(t*3).Take(3)).ToArray();Assert.That(triangles.Length,Is.GreaterThan(30));
        var source=f.Filter.sharedMesh;selected=new Mesh{vertices=source.vertices,normals=source.normals,uv=source.uv};selected.SetTriangles(triangles,0);selected.RecalculateBounds();f.Filter.sharedMesh=selected;renderer.sharedMaterial=material;renderer.shadowCastingMode=ShadowCastingMode.Off;
        var target=new Vector3(1,(f.Bounds.min.y+f.Bounds.max.y)/2,2.1275f);var camera=new Pose(target+new Vector3(0,0,above?.7f:-.7f),Quaternion.LookRotation(above?Vector3.back:Vector3.forward,Vector3.down));overlay.transform.SetPositionAndRotation(camera.position,camera.rotation);
        overlay.projectionMatrix=Matrix4x4.Frustum(-960f/1332.7f*.01f,960f/1332.7f*.01f,-540f/1332.7f*.01f,540f/1332.7f*.01f,.01f,100);overlay.worldToCameraMatrix=DjiSharedReferenceCubeOverlay.NativeOpticalToUnityViewBasis*ReferenceFrameTransforms.MatrixFromPose(ReferenceFrameTransforms.Invert(camera));overlay.cullingMatrix=overlay.projectionMatrix*overlay.worldToCameraMatrix;
        var rt=RenderTexture.GetTemporary(400,400,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB);var old=RenderTexture.active;var tex=new Texture2D(400,400,TextureFormat.RGB24,false);int red;
        try{RenderPipeline.SubmitRenderRequest(baseCamera,new RenderPipeline.StandardRequest{destination=rt});RenderTexture.active=rt;tex.ReadPixels(new Rect(0,0,400,400),0,0);tex.Apply();red=tex.GetPixels32().Count(c=>c.r>240&&c.g<10&&c.b<10);}finally{RenderTexture.active=old;RenderTexture.ReleaseTemporary(rt);Object.DestroyImmediate(tex);}
        Assert.That(red,(interior==above)?Is.GreaterThan(100):Is.EqualTo(0));
    }
}
