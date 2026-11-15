using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

public sealed class DjiOverlayCullingParityTests
{
    private static readonly Vector3[] Normals = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
    private static readonly Color[] Colors = { Color.red, Color.cyan, Color.green, Color.magenta, Color.blue, Color.yellow };
    private Scene _scene, _previous;
    private Camera _base, _overlay;
    private GameObject _cube, _canopy;
    private DjiOverlayCullingParity _marker;
    private UniversalRendererData _renderer;
    private DJIBackgroundURPFeature _background;
    private bool _backgroundActive, _invert;
    private int _quality;
    private CullingReadback _readback;
    private readonly List<Object> _owned = new();

    [SetUp]
    public void SetUp()
    {
        _previous = SceneManager.GetActiveScene();
        _scene = EditorSceneManager.OpenScene("Assets/Scenes/DroneView.unity", OpenSceneMode.Additive);
        SceneManager.SetActiveScene(_scene);
        _quality = QualitySettings.GetQualityLevel(); QualitySettings.SetQualityLevel(0, false);
        _invert = GL.invertCulling;
        _renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/Settings/Mobile_Renderer.asset");
        Assert.That(_renderer.rendererFeatures.OfType<DjiOverlayCullingParityFeature>().Count(), Is.EqualTo(1));
        _background = _renderer.rendererFeatures.OfType<DJIBackgroundURPFeature>().Single();
        _backgroundActive = _background.isActive; _background.SetActive(false);
        _base = NewCamera("Parity test base"); _base.cullingMask = 0;
        _overlay = NewCamera("Parity test overlay"); _overlay.clearFlags = CameraClearFlags.Depth;
        _overlay.GetUniversalAdditionalCameraData().renderType = CameraRenderType.Overlay;
        _base.GetUniversalAdditionalCameraData().cameraStack.Add(_overlay);
        _marker = _overlay.gameObject.AddComponent<DjiOverlayCullingParity>();
        _cube = GameObject.CreatePrimitive(PrimitiveType.Cube); _cube.layer = 2; _cube.transform.localScale = Vector3.one * .65f;
        var original = _cube.GetComponent<MeshFilter>().sharedMesh;
        var mesh = new Mesh { vertices = original.vertices, normals = original.normals, uv = original.uv, subMeshCount = 6 };
        _owned.Add(mesh);
        var lists = Normals.Select(_ => new List<int>()).ToArray();
        var triangles = original.triangles; var normals = original.normals;
        for (int i = 0; i < triangles.Length; i += 3)
            lists[Array.FindIndex(Normals, n => Vector3.Dot(n, normals[triangles[i]]) > .99f)].AddRange(triangles.Skip(i).Take(3));
        for (int i = 0; i < 6; i++) mesh.SetTriangles(lists[i], i);
        mesh.RecalculateBounds(); _cube.GetComponent<MeshFilter>().sharedMesh = mesh;
        _cube.GetComponent<MeshRenderer>().sharedMaterials = Colors.Select(c => {
            var m = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            m.SetColor("_BaseColor", c); m.SetFloat("_Cull", 2); _owned.Add(m); return m;
        }).ToArray();
        _cube.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
        SetPose(0);
    }

    [TearDown]
    public void TearDown()
    {
        GL.invertCulling = _invert;
        if (_readback != null) { _renderer.rendererFeatures.Remove(_readback); _renderer.SetDirty(); Object.DestroyImmediate(_readback); }
        if (_background != null) _background.SetActive(_backgroundActive);
        foreach (var o in _owned) if (o != null) Object.DestroyImmediate(o); _owned.Clear();
        if (_previous.IsValid() && _previous.isLoaded) SceneManager.SetActiveScene(_previous);
        if (_scene.IsValid() && _scene.isLoaded) EditorSceneManager.CloseScene(_scene, true);
        QualitySettings.SetQualityLevel(_quality, false);
    }

    private Camera NewCamera(string name)
    {
        var c = new GameObject(name, typeof(Camera), typeof(UniversalAdditionalCameraData)).GetComponent<Camera>();
        c.enabled = false; c.cullingMask = 1 << 2; c.clearFlags = CameraClearFlags.SolidColor;
        c.backgroundColor = new Color(.08f, .08f, .08f); c.allowHDR = false; c.allowMSAA = false;
        c.nearClipPlane = .01f; c.farClipPlane = 100;
        return c;
    }

    private void SetPose(int index)
    {
        var positions = new[] { new Vector3(-.6896817f,.820439041f,3.21376538f), new Vector3(-.0801293254f,1.9368732f,6.413968f), new Vector3(1.93668318f,.793717742f,4.51488256f) };
        var rotations = new[] { new Quaternion(.828000546f,.125230432f,-.0929869041f,.5385962f), new Quaternion(.728350639f,-.5550509f,.2294151f,.329837233f), new Quaternion(.174569413f,.9064738f,-.375155121f,.08419838f) };
        Pose camera;
        if (index < 3) camera = ReferenceFrameTransforms.Invert(new Pose(positions[index], rotations[index]));
        else {
            var points = new[] { new Vector3(0,0,3),new Vector3(0,0,-3),new Vector3(0,-3,0),new Vector3(0,3,0),new Vector3(2,-3,2) };
            var p = points[index - 3]; var forward = -p.normalized;
            camera = new Pose(p, Quaternion.LookRotation(forward, Mathf.Abs(Vector3.Dot(forward, Vector3.forward)) > .9f ? Vector3.up : Vector3.forward));
        }
        _overlay.transform.SetPositionAndRotation(camera.position, camera.rotation);
        _overlay.worldToCameraMatrix = DjiSharedReferenceCubeOverlay.NativeOpticalToUnityViewBasis * ReferenceFrameTransforms.MatrixFromPose(ReferenceFrameTransforms.Invert(camera));
        _overlay.projectionMatrix = Matrix4x4.Frustum(-960f/1332.7f*.01f,960f/1332.7f*.01f,-540f/1332.7f*.01f,540f/1332.7f*.01f,.01f,100);
        _overlay.cullingMatrix = _overlay.projectionMatrix * _overlay.worldToCameraMatrix;
        _overlay.enabled = true;
        _cube.transform.position = camera.position + camera.rotation * Vector3.forward * 2;
    }

    private Texture2D Render(Camera c = null)
    {
        var target = RenderTexture.GetTemporary(480,270,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB);
        var previous = RenderTexture.active;
        var image = new Texture2D(480,270,TextureFormat.RGB24,false);
        try {
            RenderPipeline.SubmitRenderRequest(c != null ? c : _base, new RenderPipeline.StandardRequest { destination = target });
            RenderTexture.active = target; image.ReadPixels(new Rect(0,0,480,270),0,0); image.Apply(); return image;
        } catch { Object.DestroyImmediate(image); throw; }
        finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target); }
    }

    private (int front, int back) CountFaces(Texture2D image, Camera camera)
    {
        var pixels = image.GetPixels32(); int front = 0, back = 0;
        for (int i = 0; i < 6; i++) {
            Color32 color = Colors[i];
            var count = pixels.Count(c => Math.Abs(c.r-color.r)<10 && Math.Abs(c.g-color.g)<10 && Math.Abs(c.b-color.b)<10);
            var center = _cube.transform.TransformPoint(Normals[i] * .5f);
            if (Vector3.Dot(Normals[i],camera.transform.position-center)>0) front+=count; else back+=count;
        }
        return (front,back);
    }

    [TestCase(0),TestCase(1),TestCase(2),TestCase(3),TestCase(4),TestCase(5),TestCase(6),TestCase(7)]
    public void ExplicitOverlay_RendersOnlyOutwardFaces_WithUnchangedMatrices(int pose)
    {
        SetPose(pose); var view=_overlay.worldToCameraMatrix; var projection=_overlay.projectionMatrix; var transform=_overlay.transform.localToWorldMatrix;
        _marker.enabled=false; var before=Render(); var wrong=CountFaces(before,_overlay); Object.DestroyImmediate(before);
        Assert.That(wrong.front,Is.Zero); Assert.That(wrong.back,Is.GreaterThan(100));
        _marker.enabled=true; var after=Render(); var correct=CountFaces(after,_overlay); Object.DestroyImmediate(after);
        Assert.That(correct.front,Is.GreaterThan(100)); Assert.That(correct.back,Is.Zero);
        Assert.That(_overlay.worldToCameraMatrix,Is.EqualTo(view)); Assert.That(_overlay.projectionMatrix,Is.EqualTo(projection)); Assert.That(_overlay.transform.localToWorldMatrix,Is.EqualTo(transform));
        Assert.That(GL.invertCulling,Is.EqualTo(_invert));
    }

    [TestCase(0),TestCase(1),TestCase(2)]
    public void OrdinaryCamera_AfterOverlayHasNoLeakedRasterState(int pose)
    {
        SetPose(pose); Object.DestroyImmediate(Render());
        _base.cullingMask=1<<2; _base.GetUniversalAdditionalCameraData().cameraStack.Clear();
        _base.transform.SetPositionAndRotation(_overlay.transform.position,_overlay.transform.rotation);
        _base.projectionMatrix=_overlay.projectionMatrix; _base.ResetWorldToCameraMatrix();
        var image=Render(); var faces=CountFaces(image,_base); Object.DestroyImmediate(image);
        Assert.That(faces.front,Is.GreaterThan(100)); Assert.That(faces.back,Is.Zero);
    }

    [Test]
    public void OnlyMarkedActiveOverlayWithProperRotationViewIsEligible()
    {
        Assert.That(DjiOverlayCullingParityFeature.ShouldCorrect(_overlay,CameraRenderType.Overlay),Is.True);
        Assert.That(DjiOverlayCullingParityFeature.ShouldCorrect(_overlay,CameraRenderType.Base),Is.False);
        _marker.enabled=false; Assert.That(DjiOverlayCullingParityFeature.ShouldCorrect(_overlay,CameraRenderType.Overlay),Is.False);
        _marker.enabled=true; _overlay.ResetWorldToCameraMatrix(); Assert.That(DjiOverlayCullingParityFeature.ShouldCorrect(_overlay,CameraRenderType.Overlay),Is.False);
    }

    [Test]
    public void DisabledOverlayAndRecreatedMarker_DoNotRetainState()
    {
        Object.DestroyImmediate(Render()); _overlay.enabled=false;
        var blank=Render(); Assert.That(blank.GetPixels32().Select(p=>p.r).Distinct().Count(),Is.LessThan(3)); Object.DestroyImmediate(blank);
        _overlay.enabled=true; Object.DestroyImmediate(_marker); _marker=_overlay.gameObject.AddComponent<DjiOverlayCullingParity>();
        var image=Render(); var faces=CountFaces(image,_overlay); Object.DestroyImmediate(image);
        Assert.That(faces.back,Is.Zero); Assert.That(faces.front,Is.GreaterThan(100)); Assert.That(GL.invertCulling,Is.EqualTo(_invert));
    }

    [Test]
    public void TransparentOneSidedGeometry_UsesSameCorrectParity()
    {
        foreach(var m in _cube.GetComponent<MeshRenderer>().sharedMaterials) {
            m.SetFloat("_Surface",1); m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); m.renderQueue=3000;
            m.SetFloat("_SrcBlend",(float)BlendMode.SrcAlpha);m.SetFloat("_DstBlend",(float)BlendMode.OneMinusSrcAlpha);m.SetFloat("_ZWrite",0);
        }
        var image=Render(); var faces=CountFaces(image,_overlay); Object.DestroyImmediate(image);
        Assert.That(faces.front,Is.GreaterThan(100));Assert.That(faces.back,Is.Zero);
    }

    [TestCase(0),TestCase(1),TestCase(2)]
    public void DepthPrepass_MatchesFixedShadowVisibleCullReference(int pose)
    {
        SetPose(pose);
        AddReadback(true); var before=Render(); Object.DestroyImmediate(before); var corrected=_readback.DepthPixels();
        _marker.enabled=false; foreach(var m in _cube.GetComponent<MeshRenderer>().sharedMaterials)m.SetFloat("_Cull",1);
        var reference=Render(); Object.DestroyImmediate(reference); var expected=_readback.DepthPixels();
        Assert.That(corrected.Max()-corrected.Min(),Is.GreaterThan(.00001f),"Depth must contain actual geometry, not a clear texture");
        Assert.That(MaxDifference(corrected,expected),Is.LessThan(1e-6f));
        var output=Environment.GetEnvironmentVariable("DJI_CULLING_FIXTURE_DIR");
        if(!string.IsNullOrEmpty(output))File.WriteAllText(Path.Combine(output,"gpu-depth-validation-"+pose+".json"),
            "{\"depthPrepassMaxDifference\":"+MaxDifference(corrected,expected).ToString("R",System.Globalization.CultureInfo.InvariantCulture)+
            ",\"depthRange\":"+(corrected.Max()-corrected.Min()).ToString("R",System.Globalization.CultureInfo.InvariantCulture)+"}");
    }

    [TestCase(0),TestCase(1),TestCase(2)]
    public void ActualCanopy_ShadowAtlasUnchanged_AndVisibleOutputMatchesFixedCasterControl(int pose)
    {
        _cube.SetActive(false);_canopy=RoofCanopyLightingPreview.CreateContent();SetPose(pose);AddReadback(false);
        var renderers=_canopy.GetComponentsInChildren<MeshRenderer>();
        _marker.enabled=false;Object.DestroyImmediate(Render());var originalShadow=_readback.ShadowPixels();
        _marker.enabled=true;var actual=Render();var correctedShadow=_readback.ShadowPixels();
        Assert.That(originalShadow.Max()-originalShadow.Min(),Is.GreaterThan(.00001f),"Shadow atlas must contain actual shadow casters");
        Assert.That(MaxDifference(originalShadow,correctedShadow),Is.LessThan(1e-6f));
        _marker.enabled=false;
        foreach(var r in renderers) {
            if(r.shadowCastingMode!=ShadowCastingMode.Off) {
                var child=new GameObject("Fixed original shadow caster",typeof(MeshFilter),typeof(MeshRenderer));child.layer=2;child.transform.SetParent(r.transform,false);
                child.GetComponent<MeshFilter>().sharedMesh=r.GetComponent<MeshFilter>().sharedMesh;
                var caster=child.GetComponent<MeshRenderer>();caster.sharedMaterial=r.sharedMaterial;caster.shadowCastingMode=ShadowCastingMode.ShadowsOnly;
            }
            r.shadowCastingMode=ShadowCastingMode.Off;var clone=new Material(r.sharedMaterial);_owned.Add(clone);
            if(clone.GetFloat("_Cull")!=0)clone.SetFloat("_Cull",1);r.sharedMaterial=clone;
        }
        var reference=Render();var colorDifference=MaxDifference(actual.GetPixels(),reference.GetPixels());Assert.That(colorDifference,Is.LessThan(1f/255f));
        var output=Environment.GetEnvironmentVariable("DJI_CULLING_FIXTURE_DIR");
        if(!string.IsNullOrEmpty(output))File.WriteAllText(Path.Combine(output,"gpu-shadow-validation-"+pose+".json"),
            "{\"shadowAtlasMaxDifference\":"+MaxDifference(originalShadow,correctedShadow).ToString("R",System.Globalization.CultureInfo.InvariantCulture)+
            ",\"shadowAtlasRange\":"+(originalShadow.Max()-originalShadow.Min()).ToString("R",System.Globalization.CultureInfo.InvariantCulture)+
            ",\"fixedShadowReferenceMaxColorDifference\":"+colorDifference.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+"}");
        Object.DestroyImmediate(actual);Object.DestroyImmediate(reference);
    }

    [Test]
    public void RendererDisposalAndRecreation_PreservesEligibilityAndGlobalState()
    {
        Object.DestroyImmediate(Render());_renderer.SetDirty();
        var image=Render();var faces=CountFaces(image,_overlay);Object.DestroyImmediate(image);
        Assert.That(faces.front,Is.GreaterThan(100));Assert.That(faces.back,Is.Zero);Assert.That(GL.invertCulling,Is.EqualTo(_invert));
    }

    [TestCase(true),TestCase(false)]
    public void ActualWoodPostCap_IsCulledAboveAndVisibleBelow(bool above)
    {
        _cube.SetActive(false);_canopy=RoofCanopyLightingPreview.CreateContent();
        var renderers=_canopy.GetComponentsInChildren<MeshRenderer>();
        var wood=renderers.Single(r=>r.name=="wood");var source=wood.GetComponent<MeshFilter>().sharedMesh;
        foreach(var r in renderers)r.enabled=false;
        var vertices=source.vertices;var normals=source.normals;var triangles=source.triangles;var selected=new List<int>();
        var normalMatrix=wood.transform.localToWorldMatrix.inverse.transpose;
        for(int i=0;i<triangles.Length;i+=3) {
            var center=wood.transform.TransformPoint((vertices[triangles[i]]+vertices[triangles[i+1]]+vertices[triangles[i+2]])/3);
            var normal=normalMatrix.MultiplyVector(normals[triangles[i]]).normalized;
            if(center.x<.3f&&center.y<-.9f&&center.z<.01f&&normal.z<-.99f)selected.AddRange(triangles.Skip(i).Take(3));
        }
        Assert.That(selected.Count,Is.GreaterThanOrEqualTo(6),"Must identify the actual front post bottom, not a synthetic cap");
        var mesh=new Mesh{vertices=vertices,normals=normals,uv=source.uv};mesh.SetTriangles(selected,0);mesh.RecalculateBounds();_owned.Add(mesh);
        var cap=new GameObject("Actual FBX post cap",typeof(MeshFilter),typeof(MeshRenderer));cap.layer=2;cap.transform.SetParent(wood.transform,false);
        cap.GetComponent<MeshFilter>().sharedMesh=mesh;var material=new Material(Shader.Find("Universal Render Pipeline/Unlit"));material.SetColor("_BaseColor",Color.red);material.SetFloat("_Cull",2);_owned.Add(material);
        cap.GetComponent<MeshRenderer>().sharedMaterial=material;
        var target=selected.Select(i=>wood.transform.TransformPoint(vertices[i])).Aggregate(Vector3.zero,(sum,v)=>sum+v)/selected.Count;
        var position=target+new Vector3(.28f,-.28f,above?.18f:-.18f);
        var camera=new Pose(position,Quaternion.LookRotation(target-position,Vector3.back));
        _overlay.transform.SetPositionAndRotation(camera.position,camera.rotation);
        _overlay.worldToCameraMatrix=DjiSharedReferenceCubeOverlay.NativeOpticalToUnityViewBasis*ReferenceFrameTransforms.MatrixFromPose(ReferenceFrameTransforms.Invert(camera));
        _overlay.cullingMatrix=_overlay.projectionMatrix*_overlay.worldToCameraMatrix;
        var image=Render();var red=image.GetPixels32().Count(c=>c.r>240&&c.g<10&&c.b<10);Object.DestroyImmediate(image);
        Assert.That(red,above?Is.EqualTo(0):Is.GreaterThan(30));
    }

    [Test]
    public void ExistingGlobalFlag_IsRestoredRatherThanForcedFalse()
    {
        GL.invertCulling=true;Object.DestroyImmediate(Render());Assert.That(GL.invertCulling,Is.True);
    }

    [Test]
    public void OverlayCameraRecreation_RestoresCorrectionWithoutStaleRegistration()
    {
        Object.DestroyImmediate(Render());var position=_overlay.transform.position;var rotation=_overlay.transform.rotation;
        var view=_overlay.worldToCameraMatrix;var projection=_overlay.projectionMatrix;
        _base.GetUniversalAdditionalCameraData().cameraStack.Clear();Object.DestroyImmediate(_overlay.gameObject);
        _overlay=NewCamera("Recreated overlay");_overlay.GetUniversalAdditionalCameraData().renderType=CameraRenderType.Overlay;
        _overlay.clearFlags=CameraClearFlags.Depth;_overlay.enabled=true;_marker=_overlay.gameObject.AddComponent<DjiOverlayCullingParity>();
        _overlay.transform.SetPositionAndRotation(position,rotation);_overlay.worldToCameraMatrix=view;_overlay.projectionMatrix=projection;
        _overlay.cullingMatrix=projection*view;_base.GetUniversalAdditionalCameraData().cameraStack.Add(_overlay);
        var image=Render();var faces=CountFaces(image,_overlay);Object.DestroyImmediate(image);
        Assert.That(faces.front,Is.GreaterThan(100));Assert.That(faces.back,Is.Zero);Assert.That(GL.invertCulling,Is.EqualTo(_invert));
    }

    private void AddReadback(bool depth)
    {
        _readback=ScriptableObject.CreateInstance<CullingReadback>();_readback.camera=_overlay;_readback.needDepthPrepass=depth;_readback.Create();
        _renderer.rendererFeatures.Add(_readback);_renderer.SetDirty();
    }
    private static float MaxDifference(float[] a,float[] b)=>a.Zip(b,(x,y)=>Mathf.Abs(x-y)).Max();
    private static float MaxDifference(Color[] a,Color[] b)=>a.Zip(b,(x,y)=>Mathf.Max(Mathf.Abs(x.r-y.r),Mathf.Abs(x.g-y.g),Mathf.Abs(x.b-y.b))).Max();
}

// Editor-only GPU readback. Never included in or attached to a player renderer.
public sealed class CullingReadback : ScriptableRendererFeature
{
    public Camera camera; public bool needDepthPrepass;
    private Pass _pass; private Material _material; private RenderTexture _depth,_shadow;private RTHandle _depthHandle,_shadowHandle;
    public override void Create()
    {
        if(_material!=null)return;
        _material=new Material(Shader.Find("Hidden/DJI/CullingAuditDepth"));
        _depth=new RenderTexture(256,256,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear);_depth.Create();
        _shadow=new RenderTexture(256,256,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear);_shadow.Create();
        _depthHandle=RTHandles.Alloc(_depth);_shadowHandle=RTHandles.Alloc(_shadow);
        _pass=new Pass(this){renderPassEvent=(RenderPassEvent)((int)RenderPassEvent.AfterRenderingTransparents+1)};
    }
    public override void AddRenderPasses(ScriptableRenderer renderer,ref RenderingData data)
    {
        if(data.cameraData.camera!=camera)return;
        _pass.ConfigureInput(needDepthPrepass?ScriptableRenderPassInput.Normal:ScriptableRenderPassInput.None);renderer.EnqueuePass(_pass);
    }
    public float[] DepthPixels()=>Read(_depth);public float[] ShadowPixels()=>Read(_shadow);
    private static float[] Read(RenderTexture rt){var old=RenderTexture.active;var t=new Texture2D(256,256,TextureFormat.RGBAFloat,false,true);try{RenderTexture.active=rt;t.ReadPixels(new Rect(0,0,256,256),0,0);t.Apply();return t.GetPixels().Select(c=>c.r).ToArray();}finally{RenderTexture.active=old;Object.DestroyImmediate(t);}}
    protected override void Dispose(bool disposing){_depthHandle?.Release();_shadowHandle?.Release();if(_depth!=null)Object.DestroyImmediate(_depth);if(_shadow!=null)Object.DestroyImmediate(_shadow);if(_material!=null)Object.DestroyImmediate(_material);}
    private sealed class Pass:ScriptableRenderPass
    {
        private readonly CullingReadback _owner;public Pass(CullingReadback owner)=>_owner=owner;
        private sealed class Data{public TextureHandle source;public Material material;public Vector4 size;}
        public override void RecordRenderGraph(RenderGraph graph,ContextContainer frame)
        {
            var resources=frame.Get<UniversalResourceData>();Copy(graph,resources.activeDepthTexture,_owner._depthHandle);
            if(resources.mainShadowsTexture.IsValid())Copy(graph,resources.mainShadowsTexture,_owner._shadowHandle);
        }
        private void Copy(RenderGraph graph,TextureHandle source,RTHandle destination)
        {
            using var builder=graph.AddRasterRenderPass<Data>("Test depth/shadow readback",out var data);
            data.source=source;data.material=_owner._material;var d=graph.GetTextureDesc(source);data.size=new Vector4(d.width,d.height,0,0);
            builder.UseTexture(source,AccessFlags.Read);builder.SetRenderAttachment(graph.ImportTexture(destination),0,AccessFlags.Write);builder.AllowGlobalStateModification(true);builder.AllowPassCulling(false);
            builder.SetRenderFunc(static(Data d,RasterGraphContext ctx)=>{ctx.cmd.SetGlobalTexture("_AuditDepth",d.source);ctx.cmd.SetGlobalVector("_AuditDepthSize",d.size);CoreUtils.DrawFullScreen(ctx.cmd,d.material);});
        }
    }
}
