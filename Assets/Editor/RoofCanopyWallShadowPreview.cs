using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>Offline experiment preparation and render evidence; never saves the production scene.</summary>
public static class RoofCanopyWallShadowPreview
{
    private const string ConfigPath = "Assets/Resources/RoofCanopyWallShadowExperiment.asset";
    [Serializable] private sealed class Evidence
    {
        public Vector3 ray, lightRay;
        public float wallOffset, rearEndSpreadM, rearEndFaceDepthM, roofOverhangMinM, roofOverhangMaxM, rearPostPenetrationM;
        public float[] rafterContactY;
        public int groundWidth, groundHeight, wallWidth, wallHeight, darkerPhotoPixels, unchangedPhotoPixels, brighterPhotoPixels;
        public long additionalWallMaskBytes;
        public double groundBakeMs, wallBakeMs;
        public bool modelUnchanged, cameraIndependent, photoComposite, nativeOesVerified;
        public string sourceFbxSha256;
    }
    private static int _ticks;
    public static void Run() { EditorApplication.update += Deferred; }
    private static void Deferred()
    {
        if (++_ticks < 5) return;
        EditorApplication.update -= Deferred;
        try { Capture(); EditorApplication.Exit(0); }
        catch (Exception error) { UnityEngine.Debug.LogException(error); EditorApplication.Exit(1); }
    }
    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }

    private static RoofCanopyWallShadowExperiment Geometry(GameObject content, RoofCanopyShadowSettings source, Evidence evidence)
    {
        var wood = content.GetComponentsInChildren<MeshFilter>().Single(f => f.name == "wood");
        var points = RoofCanopyShadowPreparation.Points(wood);
        var keys = new Dictionary<Vector3Int,int>(); var ids = new int[points.Length];
        for (int i=0;i<points.Length;i++)
        {
            var p=points[i];var key=new Vector3Int(Mathf.RoundToInt(p.x*1e6f),Mathf.RoundToInt(p.y*1e6f),Mathf.RoundToInt(p.z*1e6f));
            if(!keys.TryGetValue(key,out var id)){id=keys.Count;keys[key]=id;}ids[i]=id;
        }
        var parent=Enumerable.Range(0,keys.Count).ToArray();
        int Root(int i){while(parent[i]!=i){parent[i]=parent[parent[i]];i=parent[i];}return i;}
        var triangles=wood.sharedMesh.triangles;
        for(int i=0;i<triangles.Length;i+=3){int r=Root(ids[triangles[i]]);parent[Root(ids[triangles[i+1]])]=r;parent[Root(ids[triangles[i+2]])]=r;}
        var groups=Enumerable.Range(0,points.Length).GroupBy(i=>Root(ids[i])).Select(g=>g.Select(i=>points[i]).Distinct().ToArray()).ToArray();
        Require(groups.Length==67,"Validated 67 wood islands required.");
        var rafters=groups.Where(g=>g.Min(p=>p.z)>1.9f && g.Max(p=>p.y)-g.Min(p=>p.y)>1.3f &&
            g.Max(p=>p.x)-g.Min(p=>p.x)<.12f && g.Max(p=>p.y)>-.1f).OrderBy(g=>g.Min(p=>p.x)).ToArray();
        Require(rafters.Length==5,"Expected five longitudinally distributed rear rafter attachments.");
        evidence.rafterContactY=rafters.Select(g=>g.Max(p=>p.y)).ToArray();
        evidence.rearEndSpreadM=evidence.rafterContactY.Max()-evidence.rafterContactY.Min();
        Require(evidence.rearEndSpreadM<1e-5f,"Rafter rear support line is not planar within float/import precision; do not average.");
        // Use the exterior supporting plane, not a mean or the old placeholder.
        evidence.wallOffset=evidence.rafterContactY.Max();
        evidence.rearEndFaceDepthM=rafters.Max(g=>g.Where(p=>p.y>-.1f).Max(p=>p.y)-g.Where(p=>p.y>-.1f).Min(p=>p.y));
        var roof=RoofCanopyShadowPreparation.Points(content.GetComponentsInChildren<MeshFilter>().Single(f=>f.name=="roof"));
        // The roof rear edge is the high-Y vertex of each narrow X strip.
        var edge=roof.GroupBy(p=>Mathf.FloorToInt(p.x*10)).Select(g=>g.Max(p=>p.y)).ToArray();
        evidence.roofOverhangMinM=edge.Min()-evidence.wallOffset;evidence.roofOverhangMaxM=edge.Max()-evidence.wallOffset;
        evidence.rearPostPenetrationM=points.Max(p=>p.y)-evidence.wallOffset;
        evidence.sourceFbxSha256=source.sourceFbxSha256;
        var geometry=AssetDatabase.LoadAssetAtPath<RoofCanopyWallShadowExperiment>(ConfigPath);
        if(geometry==null)
        {
            geometry=ScriptableObject.CreateInstance<RoofCanopyWallShadowExperiment>();
            geometry.geometryVerified=true;geometry.sourceFbxSha256=source.sourceFbxSha256;geometry.wallOffset=evidence.wallOffset;
            // Render coverage only, not measured building dimensions.
            geometry.wallCenterX=2.4f;geometry.wallWidth=7;geometry.wallHeight=3.2f;
            AssetDatabase.CreateAsset(geometry,ConfigPath);AssetDatabase.SaveAssets();
        }
        Require(geometry.sourceFbxSha256==source.sourceFbxSha256 && Mathf.Abs(geometry.wallOffset-evidence.wallOffset)<1e-6f,
            "Existing experiment geometry differs; do not silently overwrite.");
        return geometry;
    }

    public static void Capture()
    {
        var args=Environment.GetCommandLineArgs();int index=Array.IndexOf(args,"-shadowOutput");
        string output=index>=0?args[index+1]:Path.GetFullPath("../RoofCanopyWallShadowExperiment-20261106");Directory.CreateDirectory(output);
        var previous=SceneManager.GetActiveScene();var scene=EditorSceneManager.OpenScene("Assets/Scenes/DroneView.unity",OpenSceneMode.Additive);SceneManager.SetActiveScene(scene);
        int quality=QualitySettings.GetQualityLevel();QualitySettings.SetQualityLevel(0,false);
        var oldQuality=QualitySettings.renderPipeline;var oldGlobal=GraphicsSettings.defaultRenderPipeline;
        var owned=new List<Object>();
        try
        {
            var renderer=Object.Instantiate(AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/Settings/Mobile_Renderer.asset"));owned.Add(renderer);
            var features=renderer.rendererFeatures.ToArray();renderer.rendererFeatures.Clear();
            foreach(var f in features){var clone=Object.Instantiate(f);owned.Add(clone);if(clone is DJIBackgroundURPFeature)clone.SetActive(false);renderer.rendererFeatures.Add(clone);}
            var pipeline=Object.Instantiate(AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>("Assets/Settings/Mobile_RPAsset.asset"));owned.Add(pipeline);
            var serialized=new SerializedObject(pipeline);serialized.FindProperty("m_RendererDataList").GetArrayElementAtIndex(0).objectReferenceValue=renderer;serialized.ApplyModifiedPropertiesWithoutUndo();
            QualitySettings.renderPipeline=pipeline;GraphicsSettings.defaultRenderPipeline=pipeline;
            foreach(var c in scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<Camera>(true)))c.enabled=false;
            foreach(var c in scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<Canvas>(true)))c.enabled=false;
            var content=RoofCanopyLightingPreview.CreateContent();owned.Add(content);
            var modelRenderers=content.GetComponentsInChildren<Renderer>();
            var matrices=content.GetComponentsInChildren<Transform>().Select(t=>t.localToWorldMatrix).ToArray();
            var source=Resources.Load<RoofCanopyShadowSettings>("RoofCanopyShadowSettings");
            var evidence=new Evidence{ray=source.sunlightRayDirection,lightRay=RenderSettings.sun.transform.forward};
            Require(Vector3.Distance(evidence.ray,evidence.lightRay)<1e-6f,"Fixed C light/mask mismatch.");
            var geometry=Geometry(content,source,evidence);
            // Creating the isolated asset can refresh Editor pipeline settings.
            // Restore only the in-memory preview clones before camera-stack creation.
            GraphicsSettings.defaultRenderPipeline=pipeline;QualitySettings.renderPipeline=pipeline;
            Require(RoofCanopyWallShadowExperiment.TryCreateSettings(source,geometry,true,out var settings),"Invalid experiment geometry.");owned.Add(settings);
            var watch=Stopwatch.StartNew();var groundMask=ShadowProjectionMath.Bake(settings,settings.Ground,out evidence.groundWidth,out evidence.groundHeight);watch.Stop();evidence.groundBakeMs=watch.Elapsed.TotalMilliseconds;
            watch.Restart();var wallMask=ShadowProjectionMath.Bake(settings,settings.Wall,out evidence.wallWidth,out evidence.wallHeight);watch.Stop();evidence.wallBakeMs=watch.Elapsed.TotalMilliseconds;
            evidence.additionalWallMaskBytes=wallMask.Length;
            void SaveMask(string name,byte[] mask,int w,int h){var t=new Texture2D(w,h,TextureFormat.R8,false,true);t.LoadRawTextureData(mask);t.Apply();File.WriteAllBytes(Path.Combine(output,name+".png"),t.EncodeToPNG());Object.DestroyImmediate(t);}
            SaveMask("ground-mask",groundMask,evidence.groundWidth,evidence.groundHeight);SaveMask("wall-mask",wallMask,evidence.wallWidth,evidence.wallHeight);
            var shadows=RoofCanopyProjectedShadows.Create(content.transform,2,settings);shadows.SetVisible(true);
            var ground=content.GetComponentsInChildren<MeshRenderer>(true).Single(r=>r.name.Contains("Ground Shadow"));
            var wall=content.GetComponentsInChildren<MeshRenderer>(true).Single(r=>r.name.Contains("Wall Shadow"));
            var receiverVertices=content.GetComponentsInChildren<MeshFilter>(true).Where(f=>f.name.Contains("Shadow")).SelectMany(f=>f.sharedMesh.vertices.Select(f.transform.TransformPoint)).ToArray();
            GameObject DiagnosticSurface(string name,ShadowReceiverPlane plane,Color color)
            {
                var go=new GameObject(name,typeof(MeshFilter),typeof(MeshRenderer));owned.Add(go);go.layer=2;
                var mesh=new Mesh{vertices=new[]{plane.Point(Vector2.zero),plane.Point(Vector2.right),plane.Point(Vector2.one),plane.Point(Vector2.up)},triangles=new[]{0,1,2,0,2,3}};mesh.RecalculateBounds();owned.Add(mesh);
                var mat=new Material(Shader.Find("Universal Render Pipeline/Unlit"));owned.Add(mat);mat.SetColor("_BaseColor",color);mat.SetFloat("_Cull",0);
                go.GetComponent<MeshFilter>().sharedMesh=mesh;go.GetComponent<MeshRenderer>().sharedMaterial=mat;
                go.GetComponent<MeshRenderer>().shadowCastingMode=ShadowCastingMode.Off;go.GetComponent<MeshRenderer>().receiveShadows=false;return go;
            }
            var floor=DiagnosticSurface("Diagnostic floor",settings.Ground,new Color(.52f,.55f,.49f));
            var visibleWall=DiagnosticSurface("Diagnostic wall ONLY",settings.Wall,new Color(.72f,.69f,.65f));
            var baseCamera=new GameObject("Wall preview base",typeof(Camera),typeof(UniversalAdditionalCameraData)).GetComponent<Camera>();owned.Add(baseCamera.gameObject);
            baseCamera.enabled=false;baseCamera.clearFlags=CameraClearFlags.SolidColor;baseCamera.backgroundColor=new Color(.38f,.42f,.45f);baseCamera.cullingMask=0;baseCamera.allowHDR=false;baseCamera.allowMSAA=false;
            var overlay=new GameObject("Wall preview optical",typeof(Camera),typeof(UniversalAdditionalCameraData),typeof(DjiOverlayCullingParity)).GetComponent<Camera>();owned.Add(overlay.gameObject);
            overlay.enabled=true;overlay.clearFlags=CameraClearFlags.Depth;overlay.cullingMask=1<<2;overlay.allowHDR=false;overlay.allowMSAA=false;
            overlay.GetUniversalAdditionalCameraData().renderType=CameraRenderType.Overlay;
            overlay.GetUniversalAdditionalCameraData().SetRenderer(0);baseCamera.GetUniversalAdditionalCameraData().SetRenderer(0);
            baseCamera.GetUniversalAdditionalCameraData().cameraStack.Add(overlay);
            void View(Vector3 position,Vector3 target)
            {
                var pose=new Pose(position,Quaternion.LookRotation(target-position,-Vector3.forward));overlay.transform.SetPositionAndRotation(pose.position,pose.rotation);
                overlay.worldToCameraMatrix=DjiSharedReferenceCubeOverlay.NativeOpticalToUnityViewBasis*ReferenceFrameTransforms.MatrixFromPose(ReferenceFrameTransforms.Invert(pose));
                overlay.projectionMatrix=Matrix4x4.Frustum(-960f/1332.7f*.01f,960f/1332.7f*.01f,-540f/1332.7f*.01f,540f/1332.7f*.01f,.01f,100);
                overlay.cullingMatrix=overlay.projectionMatrix*overlay.worldToCameraMatrix;
            }
            Color32[] Image(string name)
            {
                var rt=RenderTexture.GetTemporary(1600,900,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB);var old=RenderTexture.active;var t=new Texture2D(1600,900,TextureFormat.RGB24,false);
                try{RenderPipeline.SubmitRenderRequest(baseCamera,new RenderPipeline.StandardRequest{destination=rt});RenderTexture.active=rt;t.ReadPixels(new Rect(0,0,1600,900),0,0);t.Apply();File.WriteAllBytes(Path.Combine(output,name+".png"),t.EncodeToPNG());return t.GetPixels32();}
                finally{RenderTexture.active=old;RenderTexture.ReleaseTemporary(rt);Object.DestroyImmediate(t);}
            }
            View(new Vector3(6,-5,4),new Vector3(2.4f,-.5f,1.2f));Image("01-overview");
            View(new Vector3(7,-2.6f,2.4f),new Vector3(2.4f,-.2f,1.1f));Image("02-side");
            View(new Vector3(2.4f,-7,2.4f),new Vector3(2.4f,0,1.3f));Image("03-front");
            ground.enabled=false;Image("04-wall-shadow-only");ground.enabled=true;wall.enabled=false;Image("05-ground-shadow-only");wall.enabled=true;
            // Image-based alpha composition through the same stacked URP cameras.
            // This is NOT a physically registered camera pose or a live native OES test.
            floor.SetActive(false);visibleWall.SetActive(false);
            var photo=new Texture2D(2,2);owned.Add(photo);Require(photo.LoadImage(File.ReadAllBytes("C:/Users/betti/Desktop/100MEDIA/DJI_0056.JPG")),"Reference photo missing.");
            var bg=GameObject.CreatePrimitive(PrimitiveType.Quad);owned.Add(bg);Object.DestroyImmediate(bg.GetComponent<Collider>());bg.layer=0;bg.transform.localScale=new Vector3(16,9,1);
            var bgMaterial=new Material(Shader.Find("Universal Render Pipeline/Unlit"));owned.Add(bgMaterial);bgMaterial.SetTexture("_BaseMap",photo);bgMaterial.SetFloat("_Cull",0);bg.GetComponent<Renderer>().sharedMaterial=bgMaterial;
            baseCamera.cullingMask=1;baseCamera.transform.position=new Vector3(0,0,-10);baseCamera.orthographic=true;baseCamera.orthographicSize=4.5f;baseCamera.aspect=16f/9;
            Image("06-transparent-wall-photo-preview");
            foreach(var r in modelRenderers)r.enabled=false;shadows.SetVisible(false);var clear=Image("07-photo-clear");shadows.SetVisible(true);var shadowed=Image("08-photo-shadow-only");
            for(int i=0;i<clear.Length;i++)
            {
                if(shadowed[i].Equals(clear[i]))evidence.unchangedPhotoPixels++;
                if(shadowed[i].r<clear[i].r-2||shadowed[i].g<clear[i].g-2||shadowed[i].b<clear[i].b-2)evidence.darkerPhotoPixels++;
                if(shadowed[i].r>clear[i].r+1||shadowed[i].g>clear[i].g+1||shadowed[i].b>clear[i].b+1)evidence.brighterPhotoPixels++;
            }
            Require(evidence.darkerPhotoPixels>100 && evidence.unchangedPhotoPixels>clear.Length*.7 && evidence.brighterPhotoPixels==0,"Photo alpha composite failed.");
            evidence.photoComposite=true;evidence.nativeOesVerified=false;
            foreach(var r in modelRenderers)r.enabled=true;View(new Vector3(3,-5,3),new Vector3(2.4f,-.3f,1));Image("09-moved-camera-photo");
            evidence.modelUnchanged=matrices.SequenceEqual(content.GetComponentsInChildren<Transform>().Where(t=>!t.name.Contains("Shadow")).Select(t=>t.localToWorldMatrix));
            var after=content.GetComponentsInChildren<MeshFilter>(true).Where(f=>f.name.Contains("Shadow")).SelectMany(f=>f.sharedMesh.vertices.Select(f.transform.TransformPoint)).ToArray();
            evidence.cameraIndependent=receiverVertices.SequenceEqual(after)&&shadows.GeometryBuildCount==1&&Vector3.Distance(RenderSettings.sun.transform.forward,evidence.lightRay)<1e-7;
            Require(evidence.modelUnchanged&&evidence.cameraIndependent,"Model or receivers moved with camera.");
            File.WriteAllText(Path.Combine(output,"wall-shadow-evidence.json"),JsonUtility.ToJson(evidence,true));
            UnityEngine.Debug.Log("WALL_SHADOW_OFFLINE_COMPLETE noNativeOesHardwareClaim=true");
        }
        finally
        {
            QualitySettings.renderPipeline=oldQuality;GraphicsSettings.defaultRenderPipeline=oldGlobal;
            foreach(var o in owned.AsEnumerable().Reverse())if(o!=null)Object.DestroyImmediate(o);
            if(previous.IsValid()&&previous.isLoaded)SceneManager.SetActiveScene(previous);EditorSceneManager.CloseScene(scene,true);QualitySettings.SetQualityLevel(quality,false);
        }
    }
}
