using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

/// <summary>Build lightweight box proxies once in the Editor; never changes the source mesh or prefab.</summary>
public static class RoofCanopyShadowPreparation
{
    public const string SettingsPath = "Assets/Resources/RoofCanopyShadowSettings.asset";
    public const string MaterialPath = "Assets/Resources/RoofCanopyShadow.mat";
    public static void Prepare()
    {
        var output = OutputDirectory(); Directory.CreateDirectory(output);
        if (AssetDatabase.LoadAssetAtPath<RoofCanopyShadowSettings>(SettingsPath) != null)
            throw new InvalidOperationException("Shadow settings already exist; do not overwrite calibrated values.");
        var content = RoofCanopyLightingPreview.CreateContent();
        try
        {
            var wood = content.GetComponentsInChildren<MeshFilter>().Single(f => f.name == "wood");
            var roof = content.GetComponentsInChildren<MeshFilter>().Single(f => f.name == "roof");
            var points = Points(wood); var indices = wood.sharedMesh.triangles;
            var welded = new Dictionary<Vector3Int,int>(); var ids = new int[points.Length];
            for (int i = 0; i < points.Length; i++) {
                var p = points[i]; var key = new Vector3Int(Mathf.RoundToInt(p.x*1e6f),Mathf.RoundToInt(p.y*1e6f),Mathf.RoundToInt(p.z*1e6f));
                if (!welded.TryGetValue(key,out var id)) { id = welded.Count; welded[key] = id; } ids[i] = id;
            }
            var parent = Enumerable.Range(0,welded.Count).ToArray();
            int Root(int n) { while (parent[n] != n) { parent[n] = parent[parent[n]]; n = parent[n]; } return n; }
            for (int i = 0; i < indices.Length; i += 3) { int r = Root(ids[indices[i]]); parent[Root(ids[indices[i+1]])] = r; parent[Root(ids[indices[i+2]])] = r; }
            var groups = Enumerable.Range(0,points.Length).GroupBy(i => Root(ids[i])).Select(g => g.Select(i => points[i]).Distinct().ToArray()).ToArray();
            if (groups.Length != 67) throw new InvalidOperationException("Expected the validated 67 wooden islands.");
            var settings = ScriptableObject.CreateInstance<RoofCanopyShadowSettings>();
            settings.woodProxyCorners = groups.SelectMany(FitBox).ToArray();
            settings.roofProxyCorners = FitBox(Points(roof));
            settings.contacts = groups.Where(g => g.Max(p=>p.z)-g.Min(p=>p.z)>1 &&
                g.Max(p=>p.x)-g.Min(p=>p.x)<.3f && g.Max(p=>p.y)-g.Min(p=>p.y)<.3f && g.Min(p=>p.z)<.08f)
                .Select(g => new Vector3((g.Min(p=>p.x)+g.Max(p=>p.x))*.5f,(g.Min(p=>p.y)+g.Max(p=>p.y))*.5f,0)).ToArray();
            using (var hash = SHA256.Create()) settings.sourceFbxSha256 = BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(AssetDatabase.GetAssetPath(wood.sharedMesh)))).Replace("-", "");
            var shader = Shader.Find("DJI/Board Projected Shadow");
            if (shader == null || !shader.isSupported) throw new InvalidOperationException("Shadow shader unavailable.");
            var material = new Material(shader) { name = "RoofCanopy Projected Shadow", renderQueue = 2990 };
            AssetDatabase.CreateAsset(material, MaterialPath); settings.shadowMaterial = material;
            AssetDatabase.CreateAsset(settings, SettingsPath); AssetDatabase.SaveAssets();
            var watch = Stopwatch.StartNew(); var bytes = ShadowProjectionMath.Bake(settings, settings.Ground, out int w, out int h); watch.Stop();
            SaveMask(output+"/ground-mask.png", bytes, w, h);
            File.WriteAllText(output+"/proxy-and-cache-info.json", "{\"woodBoxes\":"+groups.Length+",\"woodProxyCorners\":"+settings.woodProxyCorners.Length+",\"roofBoxes\":1,\"contacts\":"+settings.contacts.Length+",\"maskWidth\":"+w+",\"maskHeight\":"+h+",\"hostBakeMilliseconds\":"+watch.Elapsed.TotalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)+",\"maskPixelsNonzero\":"+bytes.Count(b=>b>0)+",\"sourceFbxSha256\":\""+settings.sourceFbxSha256+"\"}");
            Debug.Log("ROOFCANOPY_SHADOW_PREPARED woodBoxes="+groups.Length+" wallEnabled=false wallCalibrated=false");
        }
        finally { Object.DestroyImmediate(content); }
    }
    public static string OutputDirectory()
    {
        var args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args,"-shadowOutput");
        return index >= 0 ? args[index+1] : Path.GetFullPath("../RoofCanopyStep4J-Shadows");
    }
    public static Vector3[] Points(MeshFilter filter)
    {
        using var data = MeshUtility.AcquireReadOnlyMeshData(filter.sharedMesh);
        using var vertices = new NativeArray<Vector3>(filter.sharedMesh.vertexCount,Allocator.Temp);
        data[0].GetVertices(vertices); var matrix = filter.transform.localToWorldMatrix;
        return vertices.ToArray().Select(p=>matrix.MultiplyPoint3x4(p)).ToArray();
    }
    public static Vector3[] FitBox(Vector3[] points)
    {
        var mean = points.Aggregate(Vector3.zero,(a,b)=>a+b)/points.Length;
        Vector3 Covariance(Vector3 axis) { var v = Vector3.zero; foreach (var p in points) { var d=p-mean; v+=d*Vector3.Dot(d,axis); } return v; }
        var first = points.OrderByDescending(p=>(p-mean).sqrMagnitude).First()-mean;
        if (first.sqrMagnitude < 1e-12f) throw new InvalidOperationException("Degenerate proxy.");
        first.Normalize(); for (int i=0;i<24;i++) first=Covariance(first).normalized;
        var helper = Mathf.Abs(first.x)<.7f?Vector3.right:Vector3.up;
        var second = Vector3.Cross(first,helper).normalized;
        for (int i=0;i<24;i++) { var next=Covariance(second); next-=first*Vector3.Dot(next,first); if(next.sqrMagnitude>1e-15f)second=next.normalized; }
        var third = Vector3.Cross(first,second).normalized; second=Vector3.Cross(third,first).normalized;
        var axes = new[]{first,second,third}; var lo=new float[3];var hi=new float[3];
        for(int i=0;i<3;i++){lo[i]=points.Min(p=>Vector3.Dot(p-mean,axes[i]));hi[i]=points.Max(p=>Vector3.Dot(p-mean,axes[i]));}
        var corners=new Vector3[8];for(int i=0;i<8;i++)corners[i]=mean+first*((i&1)==0?lo[0]:hi[0])+second*((i&2)==0?lo[1]:hi[1])+third*((i&4)==0?lo[2]:hi[2]);
        return corners;
    }
    private static void SaveMask(string path, byte[] mask, int w, int h)
    {
        var texture=new Texture2D(w,h,TextureFormat.RGBA32,false,true);var colors=mask.Select(b=>new Color32(b,b,b,255)).ToArray();
        texture.SetPixels32(colors);texture.Apply();File.WriteAllBytes(path,texture.EncodeToPNG());Object.DestroyImmediate(texture);
    }
}
