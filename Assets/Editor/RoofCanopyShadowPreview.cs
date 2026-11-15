using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object=UnityEngine.Object;

/// <summary>Controlled Editor renders only; temporary objects/settings are never saved to DroneView.</summary>
public static class RoofCanopyShadowPreview
{
    public static void ValidateAndCapture()
    {
        var output=Environment.GetEnvironmentVariable("DJI_SHADOW_OUTPUT_DIR")??RoofCanopyShadowPreparation.OutputDirectory();Directory.CreateDirectory(output);
        var previous=SceneManager.GetActiveScene();var scene=EditorSceneManager.OpenScene("Assets/Scenes/DroneView.unity",OpenSceneMode.Additive);SceneManager.SetActiveScene(scene);
        int quality=QualitySettings.GetQualityLevel();QualitySettings.SetQualityLevel(0,false);
        var renderer=AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/Settings/Mobile_Renderer.asset");
        var background=renderer.rendererFeatures.OfType<DJIBackgroundURPFeature>().Single();bool wasActive=background.isActive; background.SetActive(false);
        var owned=new List<Object>();var saved=new List<(string label,Color32[] pixels)>();bool invert=GL.invertCulling;
        try
        {
            foreach(var camera in scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<Camera>(true)))camera.enabled=false;
            foreach(var canvas in scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<Canvas>(true)))canvas.enabled=false;
            var content=RoofCanopyLightingPreview.CreateContent();owned.Add(content);
            var modelRenderers=content.GetComponentsInChildren<Renderer>();
            var baseCamera=new GameObject("Shadow preview base",typeof(Camera),typeof(UniversalAdditionalCameraData)).GetComponent<Camera>();owned.Add(baseCamera.gameObject);
            baseCamera.enabled=false;baseCamera.clearFlags=CameraClearFlags.SolidColor;baseCamera.backgroundColor=new Color(.48f,.52f,.47f);baseCamera.cullingMask=0;baseCamera.allowHDR=false;baseCamera.allowMSAA=false;
            var overlay=new GameObject("Shadow preview optical overlay",typeof(Camera),typeof(UniversalAdditionalCameraData),typeof(DjiOverlayCullingParity)).GetComponent<Camera>();owned.Add(overlay.gameObject);
            overlay.clearFlags=CameraClearFlags.Depth;overlay.cullingMask=1<<2;overlay.enabled=true;overlay.allowHDR=false;overlay.allowMSAA=false;
            overlay.nearClipPlane=.01f;overlay.farClipPlane=100;overlay.useOcclusionCulling=false;
            overlay.GetUniversalAdditionalCameraData().renderType=CameraRenderType.Overlay;
            baseCamera.GetUniversalAdditionalCameraData().cameraStack.Add(overlay);
            var settings=Object.Instantiate(Resources.Load<RoofCanopyShadowSettings>("RoofCanopyShadowSettings"));owned.Add(settings);settings.resolution=512;
            RoofCanopyProjectedShadows shadows=null;
            void Rebuild(bool wall)
            {
                if(shadows!=null)Object.DestroyImmediate(shadows);
                settings.wallEnabled=wall;settings.wallCalibrated=wall;
                shadows=RoofCanopyProjectedShadows.Create(content.transform,2,settings);shadows.SetVisible(true);
            }
            void Pose(Vector3 cameraPosition)
            {
                var target=new Vector3(2.4f,-.8f,1.1f);
                // Native optical +Y points down; the production proper Rx180 view conversion is reused exactly.
                var camera=new Pose(cameraPosition,Quaternion.LookRotation(target-cameraPosition,-Vector3.forward));
                overlay.transform.SetPositionAndRotation(camera.position,camera.rotation);
                overlay.worldToCameraMatrix=DjiSharedReferenceCubeOverlay.NativeOpticalToUnityViewBasis*ReferenceFrameTransforms.MatrixFromPose(ReferenceFrameTransforms.Invert(camera));
                overlay.projectionMatrix=Matrix4x4.Frustum(-960f/1332.7f*.01f,960f/1332.7f*.01f,-540f/1332.7f*.01f,540f/1332.7f*.01f,.01f,100);
                overlay.cullingMatrix=overlay.projectionMatrix*overlay.worldToCameraMatrix;
            }
            Color32[] Capture(string label,bool addToSheet=true)
            {
                var rt=RenderTexture.GetTemporary(960,540,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB);var active=RenderTexture.active;
                var image=new Texture2D(960,540,TextureFormat.RGB24,false);
                try {RenderPipeline.SubmitRenderRequest(baseCamera,new RenderPipeline.StandardRequest{destination=rt});RenderTexture.active=rt;image.ReadPixels(new Rect(0,0,960,540),0,0);image.Apply();var pixels=image.GetPixels32();File.WriteAllBytes(Path.Combine(output,label+".png"),image.EncodeToPNG());if(addToSheet)saved.Add((label,pixels));return pixels;}
                finally {RenderTexture.active=active;RenderTexture.ReleaseTemporary(rt);Object.DestroyImmediate(image);}
            }
            Pose(new Vector3(6,-5,4));Rebuild(false);shadows.SetVisible(false);var before=Capture("01-no-shadow");shadows.SetVisible(true);var ground=Capture("02-ground-shadow");
            int darker=0,brighter=0;for(int i=0;i<before.Length;i++){if(ground[i].r<before[i].r-2)darker++;if(ground[i].r>before[i].r+2)brighter++;}
            Assert.That(darker,Is.GreaterThan(100),"The projected receiver must visibly darken the existing background.");Assert.That(brighter,Is.LessThan(40),"No bright receiver or halo may be introduced.");
            var sun=RenderSettings.sun;Assert.That(sun,Is.Not.Null);
            var originalSun=sun.transform.rotation;var originalRay=settings.sunlightRayDirection;settings.sunlightRayDirection=new Vector3(.2f,.65f,-.73f).normalized;
            sun.transform.rotation=Quaternion.LookRotation(settings.sunlightRayDirection,Vector3.forward);
            Rebuild(true);var receiverObjects=content.GetComponentsInChildren<MeshRenderer>(true).Where(r=>r.name.Contains("Shadow")).ToArray();
            var groundRenderer=receiverObjects.Single(r=>r.name.Contains("Ground"));groundRenderer.enabled=false;Capture("03-wall-shadow-synthetic-calibration");groundRenderer.enabled=true;Capture("04-both-synthetic-calibration");
            foreach(var r in modelRenderers)r.enabled=false;shadows.SetVisible(false);var hidden=Capture("05-hidden");
            Assert.That(hidden.Select(p=>(p.r,p.g,p.b)).Distinct().Count(),Is.LessThan(5),"Hidden content and receivers must leave only the base image.");
            foreach(var r in modelRenderers)r.enabled=true;sun.transform.rotation=originalSun;
            settings.sunlightRayDirection=originalRay;Rebuild(false);
            Pose(new Vector3(4,-3,10));Capture("06-elevated");Pose(new Vector3(-4,-7,3));Capture("07-oblique");
            settings.sunlightRayDirection=new Vector3(.9f,-.2f,-.25f);sun.transform.rotation=Quaternion.LookRotation(settings.sunlightRayDirection,Vector3.forward);Rebuild(false);Capture("08-low-sun");
            settings.sunlightRayDirection=new Vector3(.2f,-.1f,-.97f);sun.transform.rotation=Quaternion.LookRotation(settings.sunlightRayDirection,Vector3.forward);Rebuild(false);Capture("09-high-sun");
            sun.transform.rotation=originalSun;settings.sunlightRayDirection=originalRay;Rebuild(false);Pose(new Vector3(6,-5,4));var authority=Capture("10-authority-change-same-pose");
            Assert.That(authority,Is.EqualTo(ground),"Changing a source label without changing pose cannot move or rebuild shadows.");
            // Isolate alpha composition, outside-mask transparency, and two-sided visibility without canopy occlusion.
            foreach(var r in modelRenderers)r.enabled=false;shadows.SetVisible(false);var clear=Capture("11-composite-clear",false);shadows.SetVisible(true);var mask=Capture("12-composite-shadow-only",false);
            int changed=0,unchanged=0,maxBrightening=0;for(int i=0;i<clear.Length;i++){if(mask[i].r<clear[i].r-2)changed++;else if(mask[i].Equals(clear[i]))unchanged++;maxBrightening=Mathf.Max(maxBrightening,mask[i].r-clear[i].r);}Assert.That(maxBrightening,Is.LessThanOrEqualTo(1));
            Assert.That(changed,Is.GreaterThan(100));Assert.That(unchanged,Is.GreaterThan(clear.Length*.7));
            Pose(new Vector3(3,4,5));var reverse=Capture("13-reverse-view",false);Assert.That(reverse.Any(p=>p.r<clear[0].r-2),Is.True);
            Assert.That(GL.invertCulling,Is.EqualTo(invert));
            // Four matched 960x540 captures in a labelled 2x2 sheet. Labels are supplied by the accompanying index.
            var sheet=new Texture2D(1920,1080,TextureFormat.RGB24,false);
            for(int i=0;i<4;i++)sheet.SetPixels32((i%2)*960,(1-i/2)*540,960,540,saved[i].pixels);
            sheet.Apply();File.WriteAllBytes(Path.Combine(output,"before-after-shadows.png"),sheet.EncodeToPNG());Object.DestroyImmediate(sheet);
            File.WriteAllText(Path.Combine(output,"preview-index.md"),"# Controlled Editor previews\n\nSheet top-left: no shadow. Top-right: ground. Bottom-left: wall-only with synthetic calibration and light toward wall. Bottom-right: both under that same synthetic light. Separate files 01/02 are matched production-light views; 03/04 share the synthetic wall/light configuration, not measured physical alignment. Files 05-10 cover hidden, elevated, oblique, low/high sun and an unchanged-pose authority-label control. 11-13 isolate alpha blending and reverse-view culling. No OES readback or Android hardware validation is performed.\n\n"+string.Join("\n",saved.Select(s=>s.label)));
            File.WriteAllText(Path.Combine(output,"compositing-validation.json"),"{\"darkerPixelsWithModel\":"+darker+",\"brighterPixelsWithModel\":"+brighter+",\"shadowOnlyChangedPixels\":"+changed+",\"outsideMaskUnchangedPixels\":"+unchanged+",\"cullingStateRestored\":true,\"cameraConvention\":\"production native optical Rx180 proper view with overlay parity\",\"physicalWallCalibration\":false}");
        }
        finally {
            background.SetActive(wasActive);GL.invertCulling=invert;
            foreach(var value in owned.AsEnumerable().Reverse())if(value!=null)Object.DestroyImmediate(value);
            if(previous.IsValid()&&previous.isLoaded)SceneManager.SetActiveScene(previous);
            EditorSceneManager.CloseScene(scene,true);QualitySettings.SetQualityLevel(quality,false);
        }
    }
}
