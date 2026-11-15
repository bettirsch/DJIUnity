using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using Object=UnityEngine.Object;

public sealed class RoofCanopyGeometryTests
{
    private GameObject content;
    private Vector3[] vertices,normals;
    private int[] triangles,welded;
    private List<int[]> islands;
    private MeshRenderer wood;

    [SetUp]
    public void SetUp()
    {
        content=RoofCanopyLightingPreview.CreateContent();
        wood=content.GetComponentsInChildren<MeshRenderer>().Single(r=>r.name=="wood");
        var mesh=wood.GetComponent<MeshFilter>().sharedMesh;var matrix=wood.transform.localToWorldMatrix;
        using(var data=MeshUtility.AcquireReadOnlyMeshData(mesh))
        using(var v=new NativeArray<Vector3>(mesh.vertexCount,Allocator.Temp))
        using(var n=new NativeArray<Vector3>(mesh.vertexCount,Allocator.Temp))
        {
            data[0].GetVertices(v);data[0].GetNormals(n);
            vertices=v.ToArray().Select(p=>matrix.MultiplyPoint3x4(p)).ToArray();
            normals=n.ToArray().Select(p=>matrix.inverse.transpose.MultiplyVector(p).normalized).ToArray();
        }
        triangles=mesh.triangles;
        var keys=new Dictionary<Vector3Int,int>();welded=new int[vertices.Length];
        for(int i=0;i<vertices.Length;i++)
        {
            var p=vertices[i];var key=new Vector3Int(Mathf.RoundToInt(p.x*1000000),Mathf.RoundToInt(p.y*1000000),Mathf.RoundToInt(p.z*1000000));
            if(!keys.TryGetValue(key,out int id)){id=keys.Count;keys.Add(key,id);}welded[i]=id;
        }
        var parent=Enumerable.Range(0,keys.Count).ToArray();
        int Root(int i){while(parent[i]!=i){parent[i]=parent[parent[i]];i=parent[i];}return i;}
        for(int i=0;i<triangles.Length;i+=3)
        {var a=Root(welded[triangles[i]]);parent[Root(welded[triangles[i+1]])]=a;parent[Root(welded[triangles[i+2]])]=a;}
        islands=Enumerable.Range(0,triangles.Length/3).GroupBy(i=>Root(welded[triangles[i*3]])).Select(g=>g.ToArray()).ToList();
    }
    [TearDown] public void TearDown()=>Object.DestroyImmediate(content);

    [Test]
    public void AllWoodIslands_HaveConsistentClosedExteriorWinding()
    {
        Assert.That(islands.Count,Is.EqualTo(67));
        var edges=new Dictionary<(int,int),int>();
        for(int i=0;i<triangles.Length;i+=3)
        for(int j=0;j<3;j++)
        {
            var a=welded[triangles[i+j]];var b=welded[triangles[i+(j+1)%3]];
            Assert.That(a,Is.Not.EqualTo(b));var key=(a,b);edges.TryGetValue(key,out int count);edges[key]=count+1;
        }
        foreach(var edge in edges)
        {Assert.That(edge.Value,Is.EqualTo(1));Assert.That(edges.TryGetValue((edge.Key.Item2,edge.Key.Item1),out int reverse)&&reverse==1,Is.True);}
        foreach(var island in islands)
        {
            var center=island.SelectMany(i=>new[]{vertices[triangles[i*3]],vertices[triangles[i*3+1]],vertices[triangles[i*3+2]]}).Aggregate(Vector3.zero,(a,b)=>a+b)/(island.Length*3);
            double volume=0;
            foreach(int t in island)
            {var a=vertices[triangles[t*3]]-center;var b=vertices[triangles[t*3+1]]-center;var c=vertices[triangles[t*3+2]]-center;volume+=Vector3.Dot(a,Vector3.Cross(b,c))/6.0;}
            // Unity's clockwise front-face convention gives negative signed volume in board space.
            Assert.That(volume,Is.LessThan(-1e-10));
        }
    }

    [Test]
    public void ImportedNormals_AgreeWithClockwiseExteriorFaces()
    {
        for(int i=0;i<triangles.Length;i+=3)
        {
            var a=triangles[i];var b=triangles[i+1];var c=triangles[i+2];
            var cross=Vector3.Cross(vertices[b]-vertices[a],vertices[c]-vertices[a]);
            Assert.That(cross.magnitude*.5f,Is.GreaterThan(1e-12f));
            var authored=(normals[a]+normals[b]+normals[c]).normalized;
            // Vector3.normalized flushes valid small face cross products to zero.
            // Keep the existing area floor and angular tolerance, normalize explicitly.
            Assert.That(Vector3.Dot(-cross/cross.magnitude,authored),Is.GreaterThan(.95f),"triangle="+i/3+" crossMagnitude="+cross.magnitude.ToString("R"));
        }
        Assert.That(wood.sharedMaterial.GetFloat("_Cull"),Is.EqualTo(2));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void UpperEmbeddedEnd_DoesNotProtrudeThroughMatingBeamSides(int joint)
    {
        var seed=joint==0?new Vector3(.114348680f,1.031793237f,1.659546733f):joint==1?new Vector3(4.411990643f,1.031793594f,1.659547448f):new Vector3(4.507399082f,1.012149453f,1.791944743f);
        seed=Reflected(seed);
        var nearest=Enumerable.Range(0,vertices.Length).OrderBy(i=>(vertices[i]-seed).sqrMagnitude).First();
        var island=islands.Single(g=>g.Any(t=>Enumerable.Range(0,3).Any(k=>welded[triangles[t*3+k]]==welded[nearest])));
        var points=island.SelectMany(t=>new[]{vertices[triangles[t*3]],vertices[triangles[t*3+1]],vertices[triangles[t*3+2]]}).ToArray();
        var matingNormal=joint<2?new Vector3(0,-.012955546f,-.999916073f):new Vector3(-.012950621f,-.07357896f,-.9972053f);
        var matingCenter=joint<2?new Vector3(2.263169644f,1.062388957f,2.025524974f):new Vector3(4.475135446f,.647172575f,2.147023678f);
        matingNormal=Reflected(matingNormal);matingCenter=Reflected(matingCenter);
        var embedded=points.Where(p=>Vector3.Dot(p-matingCenter,matingNormal)<-.00002f).ToArray();
        Assert.That(embedded.Length,Is.GreaterThan(0));
        var sides=joint<2?new[]{(new Vector3(0,.999916078f,-.012955170f),new Vector3(2.263169724f,1.095942242f,2.132937023f)),(new Vector3(0,-.999916100f,.012953508f),new Vector3(2.263169644f,1.031210542f,2.101421714f))}:new[]{(new Vector3(-.999899145f,.001002251f,.014166705f),new Vector3(4.443635066f,.725710332f,2.186732133f)),(new Vector3(.999899105f,-.001002360f,-.014169519f),new Vector3(4.507944107f,.725649023f,2.185901324f))};
        foreach(var p in embedded)foreach(var side in sides)Assert.That(Vector3.Dot(p-Reflected(side.Item2),Reflected(side.Item1)),Is.LessThan(.000003f));
        var insertion=embedded.Max(p=>-Vector3.Dot(p-matingCenter,matingNormal));
        Assert.That(insertion,Is.InRange(.069f,.076f),"Preserve internal insertion; do not shorten by 70 mm");
    }

    [Test]
    public void LowerJointMatingCorners_AreUnchanged()
    {
        var expected=new[]{new Vector3(.114348680f,1.031793237f,1.659546733f),new Vector3(.114333630f,1.030630946f,1.778017402f),new Vector3(.115181834f,1.096102476f,1.661365747f),new Vector3(.115166768f,1.094940066f,1.779835939f),new Vector3(4.411990643f,1.031793594f,1.659547448f),new Vector3(4.412005901f,1.030631304f,1.778017759f),new Vector3(4.411157131f,1.096102476f,1.661365986f),new Vector3(4.411172390f,1.094940305f,1.779836655f)};
        foreach(var point in expected)Assert.That(vertices.Min(p=>Vector3.Distance(p,Reflected(point))),Is.LessThan(.000002f));
    }

    [Test]
    public void PrefabMaterialsAndSemanticAnchor_ArePreserved()
    {
        Assert.That(AssetDatabase.AssetPathToGUID("Assets/Resources/ARContent/RoofAssets/roof_canopy1.prefab"),Is.EqualTo("603d57f1976837249985e1b5b2ca37c4"));
        Assert.That(vertices.Min(p=>p.magnitude),Is.LessThan(.00001f));
        Assert.That(content.GetComponentsInChildren<MeshRenderer>().Length,Is.EqualTo(3));
        Assert.That(content.GetComponentsInChildren<MeshFilter>().Any(f=>f.sharedMesh.name=="Cube"),Is.False);
        Assert.That(wood.sharedMaterial.name,Is.EqualTo("Natural Wood"));
        Assert.That(wood.sharedMaterial.GetTexture("_BaseMap").name,Is.EqualTo("natural_wood_07_color_contrast_2p0"));
        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(wood.sharedMaterial,out string guid,out long id);
        Assert.That(guid,Is.EqualTo("b3424cae4fbc33a4d9a425c85e2dddf7"));Assert.That(id,Is.EqualTo(7243282191679225090L));
    }

    [Test]
    public void RoofAndDrainage_TopologyIsUnchanged()
    {
        var filters=content.GetComponentsInChildren<MeshFilter>();
        var roof=filters.Single(f=>f.name=="roof").sharedMesh;var drainage=filters.Single(f=>f.name=="drainage").sharedMesh;
        // Same authored faces; reflection changes a few equivalent importer welds.
        Assert.That(roof.vertexCount,Is.EqualTo(1030));Assert.That(roof.triangles.Length/3,Is.EqualTo(516));
        Assert.That(drainage.vertexCount,Is.EqualTo(7892));Assert.That(drainage.triangles.Length/3,Is.EqualTo(4130));
    }
    // Approved content reflection, not a tracking/view-basis conversion.
    private static Vector3 Reflected(Vector3 point)=>new Vector3(point.x,-point.y,point.z);
}
